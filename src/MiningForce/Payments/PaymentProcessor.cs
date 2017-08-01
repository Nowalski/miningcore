﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.Metadata;
using CodeContracts;
using MiningForce.Blockchain.Bitcoin;
using MiningForce.Configuration;
using MiningForce.Extensions;
using MiningForce.Mining;
using MiningForce.Persistence;
using MiningForce.Persistence.Model;
using MiningForce.Persistence.Repositories;
using NLog;

namespace MiningForce.Payments
{
	/// <summary>
	/// Coin agnostic payment processor
	/// </summary>
    public class PaymentProcessor
    {
	    public PaymentProcessor(IComponentContext ctx, 
			IConnectionFactory cf, 
			IBlockRepository blockRepo,
		    IShareRepository shareRepo,
		    IBalanceRepository balanceRepo)
	    {
		    Contract.RequiresNonNull(ctx, nameof(ctx));
		    Contract.RequiresNonNull(cf, nameof(cf));
		    Contract.RequiresNonNull(blockRepo, nameof(blockRepo));
		    Contract.RequiresNonNull(shareRepo, nameof(shareRepo));
		    Contract.RequiresNonNull(balanceRepo, nameof(balanceRepo));

			this.ctx = ctx;
		    this.cf = cf;
		    this.blockRepo = blockRepo;
		    this.shareRepo = shareRepo;
		    this.balanceRepo = balanceRepo;
	    }

		private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
	    private readonly IComponentContext ctx;
	    private readonly IConnectionFactory cf;
	    private readonly IBlockRepository blockRepo;
	    private readonly IShareRepository shareRepo;
	    private readonly IBalanceRepository balanceRepo;
	    private ClusterConfig clusterConfig;
	    private Dictionary<CoinType, IPayoutHandler> payoutHandlers;
	    private Dictionary<PayoutScheme, IPayoutScheme> payoutSchemes;
		private readonly AutoResetEvent stopEvent = new AutoResetEvent(false);
	    private Thread thread;

	    #region API-Surface

		public void Configure(ClusterConfig clusterConfig)
		{
			this.clusterConfig = clusterConfig;
		}

		public void Start()
	    {
		    ResolvePayoutHandlers();
		    ResolvePayoutSchemes();

			thread = new Thread(async () =>
		    {
			    logger.Info(() => "Online");

				var interval = TimeSpan.FromSeconds(
					clusterConfig.PaymentProcessing.Interval > 0 ? clusterConfig.PaymentProcessing.Interval : 600);

				while (true)
				{
					try
					{
						await ProcessPoolsAsync();
				    }

				    catch (Exception ex)
				    {
					    logger.Error(ex);
				    }

					var waitResult = stopEvent.WaitOne(interval);

					// check if stop was signalled
					if (waitResult)
						break;
				}
			});

		    thread.IsBackground = true;
		    thread.Priority = ThreadPriority.AboveNormal;
		    thread.Name = "Payment Processing";
		    thread.Start();
	    }

	    public void Stop()
	    {
		    logger.Info(() => "Stopping ..");

		    stopEvent.Set();
		    thread.Join();

		    logger.Info(() => "Stopped");
		}

		#endregion // API-Surface

		private void ResolvePayoutHandlers()
	    {
		    payoutHandlers = clusterConfig.Pools
			    .Where(x => x.PaymentProcessing?.Enabled == true)
				.ToDictionary(x => x.Coin.Type, poolConfig =>
		    {
			    // resolve pool implementation supporting coin type
			    var handlerImpl = ctx.Resolve<IEnumerable<Meta<Lazy<IPayoutHandler, SupportedCoinsMetadataAttribute>>>>()
				    .First(x => x.Value.Metadata.SupportedCoins.Contains(poolConfig.Coin.Type)).Value;

			    // create and configure
				var handler = handlerImpl.Value;
			    handler.Configure(poolConfig);

				return handler;
		    });
		}

	    private void ResolvePayoutSchemes()
	    {
		    payoutSchemes = clusterConfig.Pools
			    .Where(x => x.PaymentProcessing?.Enabled == true)
			    .Select(x => x.PaymentProcessing.PayoutScheme)
			    .ToDictionary(x => x, x =>
			    {
				    var scheme = ctx.ResolveKeyed<IPayoutScheme>(x);
				    return scheme;
			    });
	    }

		private async Task ProcessPoolsAsync()
	    {
		    foreach (var pool in clusterConfig.Pools)
		    {
			    logger.Info(() => $"Processing payments for pool '{pool.Id}'");

				try
				{
				    var handler = payoutHandlers[pool.Coin.Type];
					var scheme = payoutSchemes[pool.PaymentProcessing.PayoutScheme];

//GenerateTestShares(pool.Id);
					await UpdatePoolBalancesAsync(pool, handler, scheme);
					await PayoutPoolBalancesAsync(pool, handler);
				}

				catch (Exception ex)
			    {
					logger.Error(ex, ()=> $"[{pool.Id}] Payment processing failed");
			    }
		    }
		}

	    private async Task UpdatePoolBalancesAsync(PoolConfig pool, IPayoutHandler handler, IPayoutScheme scheme)
	    {
			// get pending blockRepo for pool
		    var pendingBlocks = cf.Run(con => blockRepo.GetPendingBlocksForPool(con, pool.Id));

		    // ask handler to classify them
		    var updatedBlocks = await handler.ClassifyBlocksAsync(pendingBlocks);

//updatedBlocks.First().Status = BlockStatus.Confirmed;
//updatedBlocks.First().Reward = 19531250 / BitcoinConstants.SatoshisPerBitcoin;

		    foreach (var block in updatedBlocks.OrderBy(x => x.Created))
		    {
			    logger.Info(() => $"Processing payments for pool '{pool.Id}', block {block.Blockheight}");

			    if (block.Status == BlockStatus.Orphaned)
				    cf.RunTx((con, tx) => blockRepo.DeleteBlock(con, tx, block));
			    else
				    await scheme.UpdateBalancesAndBlockAsync(pool, handler, block);
		    }
	    }

	    private async Task PayoutPoolBalancesAsync(PoolConfig pool, IPayoutHandler handler)
	    {
			var poolBalancesOverMinimum = cf.Run(con => 
				balanceRepo.GetPoolBalancesOverThreshold(con, pool.Id, pool.PaymentProcessing.MinimumPayment));

		    if (poolBalancesOverMinimum.Length > 0)
			    await handler.PayoutAsync(poolBalancesOverMinimum);
			else
			    logger.Info(() => $"No balances over configured minimum payout for pool '{pool.Id}'");
	    }

		private void GenerateTestShares(string poolid)
	    {
#if DEBUG
		    var numShares = 10000;
		    var shareOffset = TimeSpan.FromSeconds(10);

			cf.RunTx((con, tx) =>
		    {
			    var block = new Block
			    {
				    Created = DateTime.UtcNow,
				    Id = 4,
				    Blockheight = 334324,
				    PoolId = "btc1",
				    Status = BlockStatus.Pending,
					TransactionConfirmationData = "foobar"
			    };

			    blockRepo.Insert(con, tx, block);

				var shareDate = block.Created;

				for (int i = 0; i < numShares; i++)
			    {
				    var share = new Share
				    {
					    Difficulty = (i & 1) == 0 ? 16 : 32,
					    NetworkDifficulty = 236000,
					    Blockheight = block.Blockheight,
					    IpAddress = "127.0.0.1",
					    Created = shareDate,
					    Worker = (i & 1) == 0 ? "mkeiTodVRTseFymDbgi2HAV3Re8zv3DQFf" : "n37zNp1QbtwHh9jVUThe6ZgCxvm9rdpX2f",
						PoolId = poolid,
				    };

				    shareDate -= shareOffset;

					shareRepo.Insert(con, tx, share);
			    }
			});
#endif
	    }
    }
}
