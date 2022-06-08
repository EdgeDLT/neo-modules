// Copyright (C) 2015-2021 The Neo Project.
//
// The Neo.Consensus.DBFT is free software distributed under the MIT software license,
// see the accompanying file LICENSE in the main directory of the
// project or http://www.opensource.org/licenses/mit-license.php
// for more details.
//
// Redistribution and use in source and binary forms with or without
// modifications are permitted.

using Akka.Actor;
using Neo.IO;
using Neo.Network.P2P;
using Neo.Network.P2P.Payloads;
using System;
using System.Linq;

namespace Neo.Consensus
{
    partial class ConsensusService
    {
        private bool CheckPrepareResponse(uint i)
        {
            if (context.TransactionHashes[i].Length == context.Transactions[i].Count)
            {
                // if we are the primary for this view, but acting as a backup because we recovered our own
                // previously sent prepare request, then we don't want to send a prepare response.
                if (context.IsAPrimary || context.WatchOnly) return true;

                // Check maximum block size via Native Contract policy
                if (context.GetExpectedBlockSize(i) > dbftSettings.MaxBlockSize)
                {
                    Log($"Rejected block: {context.Block[i].Index} The size exceed the policy", LogLevel.Warning);
                    RequestChangeView(ChangeViewReason.BlockRejectedByPolicy);
                    return false;
                }
                // Check maximum block system fee via Native Contract policy
                if (context.GetExpectedBlockSystemFee(i) > dbftSettings.MaxBlockSystemFee)
                {
                    Log($"Rejected block: {context.Block[i].Index} The system fee exceed the policy", LogLevel.Warning);
                    RequestChangeView(ChangeViewReason.BlockRejectedByPolicy);
                    return false;
                }

                // Timeout extension due to prepare response sent
                // around 2*15/M=30.0/5 ~ 40% block time (for M=5)
                ExtendTimerByFactor(2);

                Log($"Sending {nameof(PrepareResponse)}");
                localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakePrepareResponse(i) });
                CheckPreparations(i);
            }
            return true;
        }

        private void CheckPreCommits(uint i, bool forced = false)
        {
            if (forced || context.PreCommitPayloads[i].Count(p => p != null) >= context.M && context.TransactionHashes[i].All(p => context.Transactions[i].ContainsKey(p)))
            {
                ExtensiblePayload payload = context.MakeCommit(i);
                Log($"Sending {nameof(Commit)} to pOrF={i}");
                context.Save();
                localNode.Tell(new LocalNode.SendDirectly { Inventory = payload });
                // Set timer, so we will resend the commit in case of a networking issue
                ChangeTimer(TimeSpan.FromMilliseconds(neoSystem.Settings.MillisecondsPerBlock));
                CheckCommits(i);
            }
        }

        private void CheckCommits(uint i)
        {
            if (context.CommitPayloads[i].Count(p => context.GetMessage(p)?.ViewNumber == context.ViewNumber) >= context.M && context.TransactionHashes[i].All(p => context.Transactions[i].ContainsKey(p)))
            {
                block_received_index = context.Block[i].Index;
                block_received_time = TimeProvider.Current.UtcNow;
                Block block = context.CreateBlock(i);
                Log($"Sending {nameof(Block)}: height={block.Index} hash={block.Hash} tx={block.Transactions.Length} Id={i}");
                blockchain.Tell(block);
                return;
            }
        }

        private void CheckExpectedView(byte viewNumber)
        {
            if (context.ViewNumber >= viewNumber) return;
            var messages = context.ChangeViewPayloads.Select(p => context.GetMessage<ChangeView>(p)).ToArray();
            // if there are `M` change view payloads with NewViewNumber greater than viewNumber, then, it is safe to move
            if (messages.Count(p => p != null && p.NewViewNumber >= viewNumber) >= context.M)
            {
                if (!context.WatchOnly)
                {
                    ChangeView message = messages[context.MyIndex];
                    // Communicate the network about my agreement to move to `viewNumber`
                    // if my last change view payload, `message`, has NewViewNumber lower than current view to change
                    if (message is null || message.NewViewNumber < viewNumber)
                        localNode.Tell(new LocalNode.SendDirectly { Inventory = context.MakeChangeView(ChangeViewReason.ChangeAgreement) });
                }
                InitializeConsensus(viewNumber);
            }
        }

        private void CheckPreparations(uint pID)
        {
            int thresholdForPrep = pID == 0 ? context.F + 1 : context.M;

            if (context.PreparationPayloads[pID].Count(p => p != null) >= thresholdForPrep && context.TransactionHashes[pID].All(p => context.Transactions[pID].ContainsKey(p)))
            {
                ExtensiblePayload payload = context.MakePreCommit(pID);
                Log($"Sending {nameof(PreCommit)} pOrF={pID}");
                context.Save();
                localNode.Tell(new LocalNode.SendDirectly { Inventory = payload });
                // Set timer, so we will resend the commit in case of a networking issue
                ChangeTimer(TimeSpan.FromMilliseconds(neoSystem.Settings.MillisecondsPerBlock));
                CheckPreCommits(pID);

                // ==============================================
                // Speed-up path to also send commit
                if (pID == 0 && context.PreparationPayloads[0].Count(p => p != null) >= context.M)
                    CheckPreCommits(0, true);
                // ==============================================                    

                return;
            }
        }
    }
}
