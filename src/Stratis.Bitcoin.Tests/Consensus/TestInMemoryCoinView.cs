using System;
using System.Collections.Generic;
using System.Threading;
using NBitcoin;
using Stratis.Bitcoin.Features.Consensus.CoinViews;
using Stratis.Bitcoin.Utilities;
using ReaderWriterLock = NBitcoin.ReaderWriterLock;

namespace Stratis.Bitcoin.Tests.Consensus
{
    /// <summary>
    /// Coinview that holds all information in the memory, which is used in tests.
    /// </summary>
    /// <remarks>Rewinding is not supported in this implementation.</remarks>
    public class TestInMemoryCoinView : ICoinView
    {
        /// <summary>Lock object to protect access to <see cref="unspents"/> and <see cref="tipHash"/>.</summary>
        private readonly ReaderWriterLock lockobj = new ReaderWriterLock();

        /// <summary>Information about unspent outputs mapped by transaction IDs the outputs belong to.</summary>
        /// <remarks>All access to this object has to be protected by <see cref="lockobj"/>.</remarks>
        private readonly Dictionary<uint256, UnspentOutputs> unspents = new Dictionary<uint256, UnspentOutputs>();

        /// <summary>Hash of the block header which is the tip of the coinview.</summary>
        /// <remarks>All access to this object has to be protected by <see cref="lockobj"/>.</remarks>
        private uint256 tipHash;

        /// <summary>
        /// Initializes an instance of the object.
        /// </summary>
        /// <param name="tipHash">Hash of the block headers of the tip of the coinview.</param>
        public TestInMemoryCoinView(uint256 tipHash)
        {
            this.tipHash = tipHash;
        }

        /// <inheritdoc />
        public uint256 GetTipHash(CancellationToken cancellationToken = default(CancellationToken))
        {
            return this.tipHash;
        }

        public void UpdateTipHash(uint256 tipHash)
        {
            this.tipHash = tipHash;
        }

        /// <inheritdoc />
        public FetchCoinsResponse FetchCoins(uint256[] txIds, CancellationToken cancellationToken = default(CancellationToken))
        {
            Guard.NotNull(txIds, nameof(txIds));

            using (this.lockobj.LockRead())
            {
                var result = new UnspentOutputs[txIds.Length];
                for (int i = 0; i < txIds.Length; i++)
                {
                    result[i] = this.unspents.TryGet(txIds[i]);
                    if (result[i] != null)
                        result[i] = result[i].Clone();
                }

                return new FetchCoinsResponse(result, this.tipHash);
            }
        }

        /// <inheritdoc />
        public void SaveChanges(IList<UnspentOutputs> unspentOutputs, IEnumerable<TxOut[]> originalOutputs, uint256 oldBlockHash, uint256 nextBlockHash, int height, List<RewindData> rewindDataList = null)
        {
            Guard.NotNull(oldBlockHash, nameof(oldBlockHash));
            Guard.NotNull(nextBlockHash, nameof(nextBlockHash));
            Guard.NotNull(unspentOutputs, nameof(unspentOutputs));

            using (this.lockobj.LockWrite())
            {
                if ((this.tipHash != null) && (oldBlockHash != this.tipHash))
                    throw new InvalidOperationException("Invalid oldBlockHash");

                this.tipHash = nextBlockHash;
                foreach (UnspentOutputs unspent in unspentOutputs)
                {
                    UnspentOutputs existing;
                    if (this.unspents.TryGetValue(unspent.TransactionId, out existing))
                    {
                        existing.Spend(unspent);
                    }
                    else
                    {
                        existing = unspent.Clone();
                        this.unspents.Add(unspent.TransactionId, existing);
                    }

                    if (existing.IsPrunable)
                        this.unspents.Remove(unspent.TransactionId);
                }
            }
        }

        /// <inheritdoc />
        public uint256 Rewind()
        {
            throw new NotImplementedException();
        }

        public RewindData GetRewindData(int height)
        {
            throw new NotImplementedException();
        }
    }
}
