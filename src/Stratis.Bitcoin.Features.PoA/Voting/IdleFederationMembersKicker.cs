using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Stratis.Bitcoin.Consensus;
using Stratis.Bitcoin.EventBus;
using Stratis.Bitcoin.EventBus.CoreEvents;
using Stratis.Bitcoin.Features.PoA.Events;
using Stratis.Bitcoin.Signals;
using Stratis.Bitcoin.Utilities;

namespace Stratis.Bitcoin.Features.PoA.Voting
{
    /// <summary>
    /// Automatically schedules addition of voting data that votes for kicking federation member that
    /// didn't produce a block in <see cref="PoAConsensusOptions.FederationMemberMaxIdleTimeSeconds"/>.
    /// </summary>
    public class IdleFederationMembersKicker : IDisposable
    {
        private readonly ISignals signals;

        private readonly IKeyValueRepository keyValueRepository;

        private readonly IConsensusManager consensusManager;

        private readonly Network network;

        private readonly IFederationManager federationManager;

        private readonly ISlotsManager slotsManager;

        private readonly VotingManager votingManager;

        private readonly ILogger logger;

        private readonly IDateTimeProvider timeProvider;

        private readonly uint federationMemberMaxIdleTimeSeconds;

        private readonly PoAConsensusFactory consensusFactory;

        private SubscriptionToken blockConnectedToken, fedMemberAddedToken, fedMemberKickedToken;

        /// <remarks>Active time is updated when member is added or produced a new block.</remarks>
        private Dictionary<PubKey, uint> fedPubKeysByLastActiveTime;

        private const string fedMembersByLastActiveTimeKey = "fedMembersByLastActiveTime";

        public IdleFederationMembersKicker(ISignals signals, Network network, IKeyValueRepository keyValueRepository, IConsensusManager consensusManager,
            IFederationManager federationManager, ISlotsManager slotsManager, VotingManager votingManager, ILoggerFactory loggerFactory, IDateTimeProvider timeProvider)
        {
            this.signals = signals;
            this.network = network;
            this.keyValueRepository = keyValueRepository;
            this.consensusManager = consensusManager;
            this.federationManager = federationManager;
            this.slotsManager = slotsManager;
            this.votingManager = votingManager;
            this.timeProvider = timeProvider;

            this.consensusFactory = this.network.Consensus.ConsensusFactory as PoAConsensusFactory;
            this.logger = loggerFactory.CreateLogger(this.GetType().FullName);
            this.federationMemberMaxIdleTimeSeconds = ((PoAConsensusOptions)network.Consensus.Options).FederationMemberMaxIdleTimeSeconds;
        }

        public void Initialize()
        {
            this.blockConnectedToken = this.signals.Subscribe<BlockConnected>(this.OnBlockConnected);
            this.fedMemberAddedToken = this.signals.Subscribe<FedMemberAdded>(this.OnFedMemberAdded);
            this.fedMemberKickedToken = this.signals.Subscribe<FedMemberKicked>(this.OnFedMemberKicked);

            Dictionary<string, uint> loaded = this.keyValueRepository.LoadValueJson<Dictionary<string, uint>>(fedMembersByLastActiveTimeKey);

            if (loaded != null)
            {
                this.fedPubKeysByLastActiveTime = new Dictionary<PubKey, uint>();

                foreach (KeyValuePair<string, uint> loadedMember in loaded)
                {
                    this.fedPubKeysByLastActiveTime.Add(new PubKey(loadedMember.Key), loadedMember.Value);
                }
            }
            else
            {
                this.logger.LogDebug("No saved data found. Initializing federation data with current timestamp.");

                this.fedPubKeysByLastActiveTime = new Dictionary<PubKey, uint>();

                // Initialize with current timestamp. If we were to initialise with 0, then everyone would be wrong instantly!
                foreach (IFederationMember federationMember in this.federationManager.GetFederationMembers())
                    this.fedPubKeysByLastActiveTime.Add(federationMember.PubKey, (uint) this.timeProvider.GetAdjustedTimeAsUnixTimestamp());

                this.SaveMembersByLastActiveTime();
            }
        }

        private void OnFedMemberKicked(FedMemberKicked fedMemberKickedData)
        {
            this.fedPubKeysByLastActiveTime.Remove(fedMemberKickedData.KickedMember.PubKey);

            this.SaveMembersByLastActiveTime();
        }

        private void OnFedMemberAdded(FedMemberAdded fedMemberAddedData)
        {
            if (!this.fedPubKeysByLastActiveTime.ContainsKey(fedMemberAddedData.AddedMember.PubKey))
            {
                this.fedPubKeysByLastActiveTime.Add(fedMemberAddedData.AddedMember.PubKey, this.consensusManager.Tip.Header.Time);

                this.SaveMembersByLastActiveTime();
            }
        }

        private void OnBlockConnected(BlockConnected blockConnectedData)
        {
            // Update last active time.
            uint timestamp = blockConnectedData.ConnectedBlock.ChainedHeader.Header.Time;
            PubKey key = this.slotsManager.GetFederationMemberForTimestamp(timestamp).PubKey;
            this.fedPubKeysByLastActiveTime.AddOrReplace(key, timestamp);

            this.SaveMembersByLastActiveTime();

            // Check if any fed member was idle for too long.
            ChainedHeader tip = this.consensusManager.Tip;

            foreach (KeyValuePair<PubKey, uint> fedMemberToActiveTime in this.fedPubKeysByLastActiveTime)
            {
                uint inactiveForSeconds = tip.Header.Time - fedMemberToActiveTime.Value;

                if (inactiveForSeconds > this.federationMemberMaxIdleTimeSeconds && this.federationManager.IsFederationMember && 
                    !FederationVotingController.IsMultisigMember(this.network, fedMemberToActiveTime.Key))
                {
                    IFederationMember memberToKick = this.federationManager.GetFederationMembers().SingleOrDefault(x => x.PubKey == fedMemberToActiveTime.Key);

                    byte[] federationMemberBytes = this.consensusFactory.SerializeFederationMember(memberToKick);

                    bool alreadyKicking = this.AlreadyVotingFor(federationMemberBytes);

                    if (!alreadyKicking)
                    {
                        this.logger.LogWarning("Federation member '{0}' was inactive for {1} seconds and will be scheduled to be kicked.", fedMemberToActiveTime.Key, inactiveForSeconds);

                        this.votingManager.ScheduleVote(new VotingData()
                        {
                            Key = VoteKey.KickFederationMember,
                            Data = federationMemberBytes
                        });
                    }
                    else
                    {
                        this.logger.LogDebug("Skipping because kicking is already voted for.");
                    }
                }
            }
        }

        /// <summary>
        /// Tells us whether we have already voted to boot a federation member.
        /// </summary>
        private bool AlreadyVotingFor(byte[] federationMemberBytes)
        {
            List<Poll> finishedPolls = this.votingManager.GetFinishedPolls();

            if(finishedPolls.Any(x => !x.IsExecuted &&
                 x.VotingData.Key == VoteKey.KickFederationMember && x.VotingData.Data.SequenceEqual(federationMemberBytes) &&
                 x.PubKeysHexVotedInFavor.Contains(this.federationManager.CurrentFederationKey.PubKey.ToHex())))
            {
                // We've already voted in a finished poll.
                return true;
            }

            List<Poll> pendingPolls = this.votingManager.GetPendingPolls();

            if (pendingPolls.Any(x => x.VotingData.Key == VoteKey.KickFederationMember &&
                                       x.VotingData.Data.SequenceEqual(federationMemberBytes) &&
                                       x.PubKeysHexVotedInFavor.Contains(this.federationManager.CurrentFederationKey.PubKey.ToHex())))
            {
                // We've already voted in a pending poll.
                return true;
            }


            List<VotingData> scheduledVotes = this.votingManager.GetScheduledVotes();

            if (scheduledVotes.Any(x => x.Key == VoteKey.KickFederationMember && x.Data.SequenceEqual(federationMemberBytes)))
            {
                // We have the vote queued to be put out next time we mine a block.
                return true;
            }

            return false;
        }

        private void SaveMembersByLastActiveTime()
        {
            var dataToSave = new Dictionary<string, uint>();

            foreach (KeyValuePair<PubKey, uint> pair in this.fedPubKeysByLastActiveTime)
                dataToSave.Add(pair.Key.ToHex(), pair.Value);

            this.keyValueRepository.SaveValueJson(fedMembersByLastActiveTimeKey, dataToSave);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (this.blockConnectedToken != null)
            {
                this.signals.Unsubscribe(this.blockConnectedToken);
                this.signals.Unsubscribe(this.fedMemberAddedToken);
                this.signals.Unsubscribe(this.fedMemberKickedToken);
            }
        }
    }
}
