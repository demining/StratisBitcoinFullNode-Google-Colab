using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NBitcoin.Protocol;
using NSubstitute;
using Stratis.Bitcoin;
using Stratis.Bitcoin.Configuration;
using Stratis.Bitcoin.Consensus;
using Stratis.Bitcoin.Features.BlockStore;
using Stratis.Bitcoin.Features.PoA;
using Stratis.Bitcoin.Primitives;
using Stratis.Bitcoin.Signals;
using Stratis.Bitcoin.Tests.Common;
using Stratis.Bitcoin.Utilities;
using Stratis.Bitcoin.Utilities.JsonErrors;
using Stratis.Features.Collateral;
using Stratis.Features.FederatedPeg.Controllers;
using Stratis.Features.FederatedPeg.Interfaces;
using Stratis.Features.FederatedPeg.Models;
using Stratis.Features.FederatedPeg.SourceChain;
using Stratis.Sidechains.Networks;
using Xunit;

namespace Stratis.Features.FederatedPeg.Tests.ControllersTests
{
    public class FederationGatewayControllerTests
    {
        private readonly Network network;

        private readonly ILoggerFactory loggerFactory;

        private readonly ILogger logger;

        private readonly IDepositExtractor depositExtractor;

        private readonly IConsensusManager consensusManager;

        private readonly IFederatedPegSettings federatedPegSettings;

        private readonly CollateralFederationManager federationManager;

        private readonly IFederationWalletManager federationWalletManager;

        private readonly IKeyValueRepository keyValueRepository;

        private readonly ISignals signals;

        public FederationGatewayControllerTests()
        {
            this.network = CirrusNetwork.NetworksSelector.Regtest();

            this.loggerFactory = Substitute.For<ILoggerFactory>();
            this.logger = Substitute.For<ILogger>();
            this.loggerFactory.CreateLogger(null).ReturnsForAnyArgs(this.logger);
            this.depositExtractor = Substitute.For<IDepositExtractor>();
            this.consensusManager = Substitute.For<IConsensusManager>();
            this.federatedPegSettings = Substitute.For<IFederatedPegSettings>();
            this.federationWalletManager = Substitute.For<IFederationWalletManager>();
            this.keyValueRepository = Substitute.For<IKeyValueRepository>();
            this.signals = new Signals(this.loggerFactory, null);
            this.federationManager = new CollateralFederationManager(NodeSettings.Default(this.network), this.network, this.loggerFactory, this.keyValueRepository, this.signals);
        }

        private FederationGatewayController CreateController()
        {
            var controller = new FederationGatewayController(
                this.loggerFactory,
                this.GetMaturedBlocksProvider(),
                this.federatedPegSettings,
                this.federationWalletManager,
                this.federationManager);

            return controller;
        }

        private MaturedBlocksProvider GetMaturedBlocksProvider()
        {
            IBlockRepository blockRepository = Substitute.For<IBlockRepository>();

            blockRepository.GetBlocks(Arg.Any<List<uint256>>()).ReturnsForAnyArgs((x) =>
            {
                List<uint256> hashes = x.ArgAt<List<uint256>>(0);
                var blocks = new List<Block>();

                foreach (uint256 hash in hashes)
                {
                    blocks.Add(this.network.CreateBlock());
                }

                return blocks;
            });

            return new MaturedBlocksProvider(this.consensusManager, this.depositExtractor, this.loggerFactory);
        }

        [Fact]
        public void GetMaturedBlockDeposits_Fails_When_Block_Height_Greater_Than_Minimum_Deposit_Confirmations_Async()
        {
            ChainedHeader tip = ChainedHeadersHelper.CreateConsecutiveHeaders(5, null, true).Last();
            this.consensusManager.Tip.Returns(tip);

            FederationGatewayController controller = this.CreateController();

            // Minimum deposit confirmations : 2
            this.depositExtractor.MinimumDepositConfirmations.Returns((uint)2);

            int maturedHeight = (int)(tip.Height - this.depositExtractor.MinimumDepositConfirmations);

            // Back online at block height : 3
            // 0 - 1 - 2 - 3
            ChainedHeader earlierBlock = tip.GetAncestor(maturedHeight + 1);

            // Mature height = 2 (Chain header height (4) - Minimum deposit confirmations (2))
            IActionResult result = controller.GetMaturedBlockDeposits(new MaturedBlockRequestModel(earlierBlock.Height, 1000));

            // Block height (3) > Mature height (2) - returns error message
            var maturedBlockDepositsResult = (result as JsonResult).Value as SerializableResult<List<MaturedBlockDepositsModel>>;
            maturedBlockDepositsResult.Should().NotBeNull();
            maturedBlockDepositsResult.IsSuccess.Should().Be(false);
            maturedBlockDepositsResult.Message.Should().Be(string.Format(MaturedBlocksProvider.RetrieveBlockHeightHigherThanMaturedTipMessage, earlierBlock.Height, maturedHeight));
        }

        [Fact]
        public void GetMaturedBlockDeposits_Gets_All_Matured_Block_Deposits()
        {
            ChainedHeader tip = ChainedHeadersHelper.CreateConsecutiveHeaders(10, null, true).Last();
            this.consensusManager.Tip.Returns(tip);

            FederationGatewayController controller = this.CreateController();

            ChainedHeader earlierBlock = tip.GetAncestor(2);

            int minConfirmations = 2;
            this.depositExtractor.MinimumDepositConfirmations.Returns((uint)minConfirmations);

            int depositExtractorCallCount = 0;
            this.depositExtractor.ExtractBlockDeposits(Arg.Any<ChainedHeaderBlock>()).Returns(new MaturedBlockDepositsModel(null, null));
            this.depositExtractor.When(x => x.ExtractBlockDeposits(Arg.Any<ChainedHeaderBlock>())).Do(info =>
            {
                depositExtractorCallCount++;
            });

            this.consensusManager.GetBlockData(Arg.Any<List<uint256>>()).ReturnsForAnyArgs((x) =>
            {
                List<uint256> hashes = x.ArgAt<List<uint256>>(0);
                return hashes.Select((h) => new ChainedHeaderBlock(new Block(), earlierBlock)).ToArray();
            });

            IActionResult result = controller.GetMaturedBlockDeposits(new MaturedBlockRequestModel(earlierBlock.Height, 1000));

            result.Should().BeOfType<JsonResult>();
            var maturedBlockDepositsResult = (result as JsonResult).Value as SerializableResult<List<MaturedBlockDepositsModel>>;
            maturedBlockDepositsResult.Should().NotBeNull();
            maturedBlockDepositsResult.IsSuccess.Should().Be(true);
            maturedBlockDepositsResult.Message.Should().Be(null);

            // If the minConfirmations == 0 and this.chain.Height == earlierBlock.Height then expectedCallCount must be 1.
            int expectedCallCount = (tip.Height - minConfirmations) - earlierBlock.Height + 1;

            depositExtractorCallCount.Should().Be(expectedCallCount);
        }

        [Fact]
        public void Call_Sidechain_Gateway_Get_Info()
        {
            string redeemScript = "2 02fad5f3c4fdf4c22e8be4cfda47882fff89aaa0a48c1ccad7fa80dc5fee9ccec3 02503f03243d41c141172465caca2f5cef7524f149e965483be7ce4e44107d7d35 03be943c3a31359cd8e67bedb7122a0898d2c204cf2d0119e923ded58c429ef97c 3 OP_CHECKMULTISIG";
            string federationIps = "127.0.0.1:36201,127.0.0.1:36202,127.0.0.1:36203";
            string multisigPubKey = "03be943c3a31359cd8e67bedb7122a0898d2c204cf2d0119e923ded58c429ef97c";
            string[] args = new[] { "-sidechain", "-regtest", $"-federationips={federationIps}", $"-redeemscript={redeemScript}", $"-publickey={multisigPubKey}", "-mincoinmaturity=1", "-mindepositconfirmations=1" };
            var nodeSettings = new NodeSettings(CirrusNetwork.NetworksSelector.Regtest(), ProtocolVersion.ALT_PROTOCOL_VERSION, args: args);

            this.federationWalletManager.IsFederationWalletActive().Returns(true);

            this.federationManager.Initialize();

            var settings = new FederatedPegSettings(nodeSettings);

            var controller = new FederationGatewayController(
                this.loggerFactory,
                this.GetMaturedBlocksProvider(),
                settings,
                this.federationWalletManager,
                this.federationManager);

            IActionResult result = controller.GetInfo();

            result.Should().BeOfType<JsonResult>();
            ((JsonResult)result).Value.Should().BeOfType<FederationGatewayInfoModel>();

            var model = ((JsonResult)result).Value as FederationGatewayInfoModel;
            model.IsMainChain.Should().BeFalse();
            model.FederationMiningPubKeys.Should().Equal(((PoAConsensusOptions)CirrusNetwork.NetworksSelector.Regtest().Consensus.Options).GenesisFederationMembers.Select(keys => keys.ToString()));
            model.MultiSigRedeemScript.Should().Be(redeemScript);
            string.Join(",", model.FederationNodeIpEndPoints).Should().Be(federationIps);
            model.IsActive.Should().BeTrue();
            model.MinimumDepositConfirmations.Should().Be(1);
            model.MultisigPublicKey.Should().Be(multisigPubKey);
        }

        [Fact]
        public void Call_Mainchain_Gateway_Get_Info()
        {
            string redeemScript = "2 02fad5f3c4fdf4c22e8be4cfda47882fff89aaa0a48c1ccad7fa80dc5fee9ccec3 02503f03243d41c141172465caca2f5cef7524f149e965483be7ce4e44107d7d35 03be943c3a31359cd8e67bedb7122a0898d2c204cf2d0119e923ded58c429ef97c 3 OP_CHECKMULTISIG";
            string federationIps = "127.0.0.1:36201,127.0.0.1:36202,127.0.0.1:36203";
            string multisigPubKey = "03be943c3a31359cd8e67bedb7122a0898d2c204cf2d0119e923ded58c429ef97c";
            string[] args = new[] { "-mainchain", "-testnet", $"-federationips={federationIps}", $"-redeemscript={redeemScript}", $"-publickey={multisigPubKey}", "-mincoinmaturity=1", "-mindepositconfirmations=1" };
            var nodeSettings = new NodeSettings(CirrusNetwork.NetworksSelector.Regtest(), ProtocolVersion.ALT_PROTOCOL_VERSION, args: args);

            this.federationWalletManager.IsFederationWalletActive().Returns(true);

            var settings = new FederatedPegSettings(nodeSettings);

            var controller = new FederationGatewayController(
                this.loggerFactory,
                this.GetMaturedBlocksProvider(),
                settings,
                this.federationWalletManager,
                this.federationManager);

            IActionResult result = controller.GetInfo();

            result.Should().BeOfType<JsonResult>();
            ((JsonResult)result).Value.Should().BeOfType<FederationGatewayInfoModel>();

            var model = ((JsonResult)result).Value as FederationGatewayInfoModel;
            model.IsMainChain.Should().BeTrue();
            model.FederationMiningPubKeys.Should().BeNull();
            model.MiningPublicKey.Should().BeNull();
            model.MultiSigRedeemScript.Should().Be(redeemScript);
            string.Join(",", model.FederationNodeIpEndPoints).Should().Be(federationIps);
            model.IsActive.Should().BeTrue();
            model.MinimumDepositConfirmations.Should().Be(1);
            model.MultisigPublicKey.Should().Be(multisigPubKey);
        }
    }
}
