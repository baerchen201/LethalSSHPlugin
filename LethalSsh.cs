using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Renci.SshNet;
using UnityEngine;

namespace LethalSsh;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class LethalSsh : BaseUnityPlugin
{
    public static LethalSsh Instance { get; private set; } = null!;
    internal static new ManualLogSource Logger { get; private set; } = null!;

    internal static Harmony? Harmony { get; set; }

    public bool Active => sshClient is { IsConnected: true };

    internal SshClient? sshClient;

    internal ShellStream? sshShell;

    internal StringBuilder _sgrCloseTags = new StringBuilder();

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
                        if (arg1.Length != 2)
                        {
                            __result.displayText = "Invalid arguments";
                            break;
                        }
                        int port = 22;
                        Int32.TryParse(args[2], out port);
                        LethalSsh.Instance.sshClient = new SshClient(
                            arg1[1],
                            port,
                            arg1[0],
                            args[3]
                        );
                        LethalSsh.Instance.sshClient.Connect();
                        LethalSsh.Instance.sshShell =
                            LethalSsh.Instance.sshClient.CreateShellStream(
                                "linux",
                                20,
                                20,
                                800,
                                600,
                                1024
                            );
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

                LethalSsh.Instance._sgrCloseTags.Clear();
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

            __instance.forceScrollbarCoroutine = __instance.StartCoroutine(
                __instance.forceScrollbarDown()
            );
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
            LethalSsh.Instance._sgrCloseTags.Clear();
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
            __instance.currentText = Regex.Replace(
                Regex.Replace(
                    Regex
                        .Split(
                            (__instance.currentText + read)
                                .Split("\n")
                                .TakeLast(50)
                                .Join(null, "\n"),
                            @"\x1B\[[0-3]?[jJ]"
                        )
                        .Last(),
                    @"\x1B\[([0-9;]*)[mM]",
                    (Match m) =>
                    {
                        StringBuilder sb = new StringBuilder();

                        string[] parameters = m.Groups[1].Value.Split(";");

                        Func<string, string, bool>? next = null;
                        string _previous = null!;
                        foreach (string p in parameters)
                        {
                            Logger.LogDebug($">> Parsing ANSI code {p}");
                            if (next != null)
                            {
                                Logger.LogDebug($"Found next function, previous: {_previous}");
                                if (!next(_previous, p))
                                {
                                    _previous = p;
                                    Logger.LogDebug($"previous overwritten {_previous}");
                                }
                                else
                                {
                                    Logger.LogDebug($"previous not overwritten: {_previous}");
                                }
                                continue;
                            }

                            switch (p)
                            {
                                // Bold
                                case "1":
                                    LethalSsh.Instance._sgrCloseTags.Insert(0, "</b>");
                                    sb.Append("<b>");
                                    break;
                                // Italic
                                case "3":
                                    LethalSsh.Instance._sgrCloseTags.Insert(0, "</i>");
                                    sb.Append("<i>");
                                    break;
                                // Underline
                                case "4":
                                    LethalSsh.Instance._sgrCloseTags.Insert(0, "</u>");
                                    sb.Append("<u>");
                                    break;
                                // Strikethrough
                                case "9":
                                    LethalSsh.Instance._sgrCloseTags.Insert(0, "</s>");
                                    sb.Append("<s>");
                                    break;

                                // Text Color
                                case "30":
                                case "31":
                                case "32":
                                case "33":
                                case "34":
                                case "35":
                                case "36":
                                case "37":
                                // Bright Text Color
                                case "90":
                                case "91":
                                case "92":
                                case "93":
                                case "94":
                                case "95":
                                case "96":
                                case "97":
                                    LethalSsh.Instance._sgrCloseTags.Insert(0, "</color>");
                                    sb.Append(
                                        $"<color=#{
                                                LethalSsh.GetColorByCode(p)
                                            }>"
                                    );
                                    break;

                                // BG Color
                                case "40":
                                case "41":
                                case "42":
                                case "43":
                                case "44":
                                case "45":
                                case "46":
                                case "47":
                                // Bright BG Color
                                case "100":
                                case "101":
                                case "102":
                                case "103":
                                case "104":
                                case "105":
                                case "106":
                                case "107":
                                    LethalSsh.Instance._sgrCloseTags.Insert(0, "</mark>");
                                    sb.Append(
                                        $"<mark=#{
                                                LethalSsh.GetColorByCode(p)
                                            }55>"
                                    );
                                    break;

                                // Extended Color (8/24 bit)
                                case "38":
                                case "48":
                                    next = (_, mode) =>
                                    {
                                        switch (mode)
                                        {
                                            // 24-bit color (True color, RGB)
                                            case "2":
                                                StringBuilder rgb = new StringBuilder();
                                                next = (
                                                    (_, r) =>
                                                    {
                                                        rgb.Append(
                                                            ((byte)Int32.Parse(r)).ToString("X2")
                                                        );
                                                        next = (_, g) =>
                                                        {
                                                            rgb.Append(
                                                                ((byte)Int32.Parse(g)).ToString(
                                                                    "X2"
                                                                )
                                                            );
                                                            next = (previous, b) =>
                                                            {
                                                                rgb.Append(
                                                                    ((byte)Int32.Parse(b)).ToString(
                                                                        "X2"
                                                                    )
                                                                );
                                                                if (previous == "38")
                                                                {
                                                                    LethalSsh.Instance._sgrCloseTags.Insert(
                                                                        0,
                                                                        "</color>"
                                                                    );
                                                                    sb.Append(
                                                                        $"<color=#{rgb.ToString()}>"
                                                                    );
                                                                }
                                                                else
                                                                {
                                                                    LethalSsh.Instance._sgrCloseTags.Insert(
                                                                        0,
                                                                        "</mark>"
                                                                    );
                                                                    sb.Append(
                                                                        $"<mark=#{rgb.ToString()}55>"
                                                                    );
                                                                }
                                                                next = null;
                                                                return false;
                                                            };
                                                            return true;
                                                        };
                                                        return true;
                                                    }
                                                );
                                                break;
                                            // 8-bit color
                                            case "5":
                                                next = (
                                                    (previous, color) =>
                                                    {
                                                        if (previous == "38")
                                                        {
                                                            LethalSsh.Instance._sgrCloseTags.Insert(
                                                                0,
                                                                "</color>"
                                                            );
                                                            sb.Append(
                                                                $"<color=#{LethalSsh.Get256ColorByCode(color)}>"
                                                            );
                                                        }
                                                        else
                                                        {
                                                            LethalSsh.Instance._sgrCloseTags.Insert(
                                                                0,
                                                                "</mark>"
                                                            );
                                                            sb.Append(
                                                                $"<mark=#{LethalSsh.Get256ColorByCode(color)}55>"
                                                            );
                                                        }

                                                        next = null;
                                                        return false;
                                                    }
                                                );
                                                break;
                                        }
                                        return true;
                                    };
                                    break;

                                // Clear
                                default:
                                    string _ = LethalSsh.Instance._sgrCloseTags.ToString();
                                    LethalSsh.Instance._sgrCloseTags.Clear();
                                    return _;
                            }

                            _previous = p;
                        }

                        Logger.LogDebug($"<< Parsed \x1B[{m.Groups[1].Value}m: {sb.ToString()}");
                        return sb.ToString();
                    }
                ),
                @"\x1B\[[?;=0-9]*[a-zA-Z~]",
                ""
            );
            if (!__instance.currentText.StartsWith("\n\n\n"))
                __instance.currentText = "\n\n\n" + __instance.currentText.TrimStart('\n');
            __instance.screenText.text = __instance.currentText;
            __instance.forceScrollbarCoroutine = __instance.StartCoroutine(
                __instance.forceScrollbarDown()
            );
        }
    }

    public static string GetColorByCode(string code)
    {
        if (code.StartsWith("4"))
            code = $"3{code.Last()}";
        else if (code.StartsWith("10"))
            code = $"9{code.Last()}";
        return code switch
        {
            "30" => "000000",
            "31" => "AA0000",
            "32" => "00AA00",
            "33" => "AA5500",
            "34" => "0000AA",
            "35" => "AA00AA",
            "36" => "00AAAA",
            "37" => "AAAAAA",
            "90" => "555555",
            "91" => "FF5555",
            "92" => "55FF55",
            "93" => "FFFF55",
            "94" => "5555FF",
            "95" => "FF55FF",
            "96" => "55FFFF",
            _ => "FFFFFF",
        };
    }

    public static string Get256ColorByCode(string code)
    {
        byte color;
        try
        {
            color = (byte)Int32.Parse(code);
        }
        catch
        {
            return "FFFFFF";
        }

        if (color <= 7)
        {
            return GetColorByCode($"3{color}");
        }
        else if (color <= 15)
        {
            return GetColorByCode($"9{color - 8}");
        }
        else if (color >= 232)
        {
            return string.Concat(
                Enumerable.Repeat(
                    (Enumerable.Range(0, 24).ToArray()[color - 232] * 0x0a + 0x08).ToString("X2"),
                    3
                )
            );
        }
        else
        {
            byte[] values =
            [
                (byte)Math.Floor((color - 16f) / 36),
                (byte)Math.Floor(((color - 16f) % 36) / 6),
                (byte)Math.Floor(((color - 16f) % 6)),
            ];
            StringBuilder sb = new StringBuilder();
            foreach (byte value in values)
            {
                sb.Append(value == 0 ? "00" : (value * 40 + 55).ToString("X2"));
            }
            return sb.ToString();
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
