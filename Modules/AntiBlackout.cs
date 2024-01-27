using AmongUs.GameOptions;
using Hazel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using TOHE.Modules;
using TOHE.Roles.Neutral;

namespace TOHE;

public static class AntiBlackout
{
    ///<summary>
    /// Check num alive Impostors & Crewmates & NeutralKillers
    ///</summary>
    public static bool BlackOutIsActive => CheckBlackOut();

    ///<summary>
    /// Count alive players and check black out 
    ///</summary>
    public static bool CheckBlackOut()
    {
        List<byte> Impostors = [];
        List<byte> Crewmates = [];
        List<byte> Neutrals = [];

        foreach (var pc in Main.AllAlivePlayerControls)
        {
            if (pc.GetCustomRole().IsImpostor()) Impostors.Add(pc.PlayerId); // Impostors
            else if (Main.PlayerStates[pc.PlayerId].countTypes == CountTypes.Impostor) Impostors.Add(pc.PlayerId); // Madmates

            else if (pc.GetCustomRole().IsNK() && !pc.Is(CustomRoles.Arsonist)) Neutrals.Add(pc.PlayerId); // Neutral Killers
            else if (pc.Is(CustomRoles.Arsonist) && Options.ArsonistCanIgniteAnytime.GetBool()) Neutrals.Add(pc.PlayerId);
            else if (pc.Is(CustomRoles.Succubus)) Neutrals.Add(pc.PlayerId);

            else Crewmates.Add(pc.PlayerId);
        }

        var numAliveImpostors = Impostors.Count;
        var numAliveCrewmates = Crewmates.Count;
        var numAliveNeutrals = Neutrals.Count;

        Logger.Info($" {numAliveImpostors}", "AntiBlackout Num Alive Impostors");
        Logger.Info($" {numAliveCrewmates}", "AntiBlackout Num Alive Crewmates");
        Logger.Info($" {numAliveCrewmates}", "AntiBlackout Num Alive Neutrals");

        var BlackOutIsActive = false;

        // if all Crewmates is dead
        if (numAliveCrewmates <= 0)
            BlackOutIsActive = true;

        // if all Impostors is dead
        if (numAliveImpostors <= 0)
            BlackOutIsActive = true;

        // if num alive Impostors > or = num alive non-impostors players
        if (numAliveImpostors >= (numAliveNeutrals + numAliveCrewmates))
            BlackOutIsActive = true;

        Logger.Info($" {BlackOutIsActive}", "BlackOut Is Active");
        return BlackOutIsActive;
    }

    public static bool IsCached { get; private set; } = false;
    private static Dictionary<byte, (bool isDead, bool Disconnected)> isDeadCache = [];
    private readonly static LogHandler logger = Logger.Handler("AntiBlackout");

    public static void SetIsDead(bool doSend = true, [CallerMemberName] string callerMethodName = "")
    {
        logger.Info($"SetIsDead is called from {callerMethodName}");
        if (IsCached)
        {
            logger.Info("Please run RestoreIsDead before running SetIsDead again.");
            return;
        }
        isDeadCache.Clear();
        foreach (var info in GameData.Instance.AllPlayers.ToArray())
        {
            if (info == null) continue;
            isDeadCache[info.PlayerId] = (info.IsDead, info.Disconnected);
            info.IsDead = false;
            info.Disconnected = false;
        }
        IsCached = true;
        if (doSend) SendGameData();
    }
    public static void RestoreIsDead(bool doSend = true, [CallerMemberName] string callerMethodName = "")
    {
        logger.Info($"RestoreIsDead is called from {callerMethodName}");
        foreach (var info in GameData.Instance.AllPlayers.ToArray())
        {
            if (info == null) continue;
            if (isDeadCache.TryGetValue(info.PlayerId, out var val))
            {
                info.IsDead = val.isDead;
                info.Disconnected = val.Disconnected;
            }
        }
        isDeadCache.Clear();
        IsCached = false;
        if (doSend) SendGameData();
    }

    public static void SendGameData([CallerMemberName] string callerMethodName = "")
    {
        logger.Info($"SendGameData is called from {callerMethodName}");
        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        // The writing {} is for readability.
        writer.StartMessage(5); //0x05 GameData
        {
            writer.Write(AmongUsClient.Instance.GameId);
            writer.StartMessage(1); //0x01 Data
            {
                writer.WritePacked(GameData.Instance.NetId);
                GameData.Instance.Serialize(writer, true);

            }
            writer.EndMessage();
        }
        writer.EndMessage();

        AmongUsClient.Instance.SendOrDisconnect(writer);
        writer.Recycle();
    }
    public static void OnDisconnect(GameData.PlayerInfo player)
    {
        // Execution conditions: Client is the host, IsDead is overridden, player is already disconnected
        if (!AmongUsClient.Instance.AmHost || !IsCached || !player.Disconnected) return;
        isDeadCache[player.PlayerId] = (true, true);
        player.IsDead = player.Disconnected = false;
        SendGameData();
    }

    ///<summary>
    ///Execute the code with IsDead temporarily set back to what it should be
    ///<param name="action">Execution details</param>
    ///</summary>
    public static void TempRestore(Action action)
    {
        logger.Info("==Temp Restore==");
        // Whether TempRestore was executed with IsDead overwritten
        bool before_IsCached = IsCached;
        try
        {
            if (before_IsCached) RestoreIsDead(doSend: false);
            action();
        }
        catch (Exception ex)
        {
            logger.Warn("An exception occurred within AntiBlackout.TempRestore");
            logger.Exception(ex);
        }
        finally
        {
            if (before_IsCached) SetIsDead(doSend: false);
            logger.Info("==/Temp Restore==");
        }
    }
    public static void AntiBlackRpcVotingComplete(this MeetingHud __instance, MeetingHud.VoterState[] states, GameData.PlayerInfo exiled, bool tie)
    {
        if (AmongUsClient.Instance.AmClient)
        {
            __instance.VotingComplete(states, exiled, tie);
        }

        var sender = CustomRpcSender.Create("AntiBlack RpcVotingComplete", SendOption.None);
        foreach (var pc in Main.AllPlayerControls)
        {
            if (pc.AmOwner) continue;
            if (pc.IsModClient()) //For mod client show real result
            {
                sender.AutoStartRpc(__instance.NetId, (byte)RpcCalls.VotingComplete, pc.GetClientId());
                {
                    sender.WritePacked(states.Length);
                    foreach (MeetingHud.VoterState voterState in states)
                    {
                        sender.WriteMessageType(voterState.VoterId);
                        sender.Write(voterState.VotedForId);
                        sender.WriteEndMessage();
                    }
                    sender.Write(exiled != null ? exiled.PlayerId : byte.MaxValue);
                    sender.Write(tie);
                    sender.EndRpc();
                }
            }
            else //For vanilla client show a tie
            {
                sender.AutoStartRpc(__instance.NetId, (byte)RpcCalls.VotingComplete, pc.GetClientId());
                {
                    sender.WritePacked(states.Length);
                    foreach (MeetingHud.VoterState voterState in states)
                    {
                        sender.WriteMessageType(voterState.VoterId);
                        sender.Write(voterState.VotedForId);
                        sender.WriteEndMessage();
                    }
                    sender.Write(byte.MaxValue);
                    sender.Write(true);
                    sender.EndRpc();
                }
            }
        }
        sender.SendMessage();
    }
    public static void Reset()
    {
        logger.Info("==Reset==");
        isDeadCache ??= [];
        isDeadCache.Clear();
        IsCached = false;
        ShowExiledInfo = false;
        StoreExiledMessage = "";
    }

    public static bool ShowExiledInfo = false;
    public static string StoreExiledMessage = "";
}