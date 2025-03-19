using System;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Renci.SshNet;
using UnityEngine;

namespace LethalSsh;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("atomic.terminalapi")]
public class LethalSsh : BaseUnityPlugin
{
    public static LethalSsh Instance { get; private set; } = null!;
    internal static new ManualLogSource Logger { get; private set; } = null!;

    internal static Harmony? Harmony { get; set; }

    public bool Active => sshClient is { IsConnected: true };

    internal SshClient? sshClient;

    internal ShellStream? sshShell;

    private void Awake()
    {
        Logger = base.Logger;
        Instance = this;

        Patch();

        Logger.LogInfo($"{MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} has loaded!");
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.ParsePlayerSentence))]
    public class ParsePlayerSentencePatch
    {
        private static bool Prefix(ref Terminal __instance, ref TerminalNode __result)
        {
            string command = __instance.screenText.text.Substring(
                __instance.screenText.text.Length - __instance.textAdded
            );
            Logger.LogDebug($">> ParsePlayerSentence {command}");
            string[] args = command.Split(" ");
            if (
                LethalSsh.Instance.Active
                && LethalSsh.Instance.sshShell != null
                && LethalSsh.Instance.sshShell.CanWrite
            )
            {
                LethalSsh.Instance.sshShell.Write(command + "\n");
                return false;
            }
            if (args[0] == "ssh")
            {
                __result = ScriptableObject.CreateInstance<TerminalNode>();
                __result.clearPreviousText = true;
                string[] arg1;
                switch (args.Length)
                {
                    case 4:
                        arg1 = args[1].Split("@");
                        if (arg1.Length != 2) {
                            __result.displayText = "Invalid arguments";
                            break;
                        }
                        int port = 22;
                        Int32.TryParse(args[2], out port);
                        LethalSsh.Instance.sshClient = new SshClient(arg1[1], port, arg1[0], args[3]);
                        LethalSsh.Instance.sshClient.Connect();
                        LethalSsh.Instance.sshShell = LethalSsh.Instance.sshClient.CreateShellStream("linux", 20, 20, 800, 600, 1024);
                        __result.displayText = "";
                        break;
                    case 3:
                        arg1 = args[1].Split("@");
                        if (arg1.Length != 2)
                        {
                            __result.displayText = "Invalid arguments";
                            break;
                        }
                        LethalSsh.Instance.sshClient = new SshClient(arg1[1], arg1[0], args[2]);
                        LethalSsh.Instance.sshClient.Connect();
                        LethalSsh.Instance.sshShell =
                            LethalSsh.Instance.sshClient.CreateShellStream(
                                "linux",
                                50,
                                50,
                                800,
                                600,
                                1024
                            );

                        __result.displayText = "";
                        break;
                    default:
                        __result.displayText = "Invalid arguments\n";
                        break;
                }
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.TextChanged))]
    public class TextChangedPatch
    {
        private static bool Prefix(ref Terminal __instance, string newText)
        {
            Logger.LogDebug($">> TextChanged {newText.Split("\n").Last()}");
            if (!LethalSsh.Instance.Active)
                return true;

            if (__instance.modifyingText)
            {
                __instance.modifyingText = false;
                return false;
            }
            __instance.textAdded += newText.Length - __instance.currentText.Length;
            if (__instance.textAdded < 0)
            {
                __instance.screenText.text = __instance.currentText;
                __instance.textAdded = 0;
            }
            else
            {
                __instance.currentText = newText;
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.QuitTerminal))]
    public class QuitTerminalPatch
    {
        private static void Prefix()
        {
            Logger.LogDebug($">> QuitTerminal");
            if (!LethalSsh.Instance.Active)
                return;
            LethalSsh.Instance.sshShell!.Close();
            LethalSsh.Instance.sshClient!.Disconnect();
            LethalSsh.Instance.sshClient = null;
        }
    }

    [HarmonyPatch(typeof(Terminal), nameof(Terminal.Update))]
    public class TerminalUpdatePatch
    {
        private static void Prefix(ref Terminal __instance)
        {
            if (
                !LethalSsh.Instance.Active
                || LethalSsh.Instance.sshShell == null
                || !LethalSsh.Instance.sshShell.CanRead
            )
                return;

            string read = LethalSsh.Instance.sshShell.Read();
            if (read.Length == 0)
                return;
            string[] lines = (__instance.currentText + read).Split("\n");
            string[] split = Regex.Split(lines.TakeLast(50).Join(null, "\n"), @"\x1B\[[0-3]?[jJ]");
            __instance.currentText = Regex.Replace(split.Last(), @"\x1B\[[?;=0-9]*[a-zA-Z]", "");
            if (!__instance.currentText.StartsWith("\n\n\n"))
                __instance.currentText = "\n\n\n" + __instance.currentText.TrimStart('\n');
            __instance.screenText.text = __instance.currentText;
            __instance.forceScrollbarCoroutine = __instance.StartCoroutine(
                __instance.forceScrollbarDown()
            );
        }
    }

    internal static void Patch()
    {
        Harmony ??= new Harmony(MyPluginInfo.PLUGIN_GUID);

        Logger.LogDebug("Patching...");

        Harmony.PatchAll();

        Logger.LogDebug("Finished patching!");
    }

    internal static void Unpatch()
    {
        Logger.LogDebug("Unpatching...");

        Harmony?.UnpatchSelf();

        Logger.LogDebug("Finished unpatching!");
    }
}
