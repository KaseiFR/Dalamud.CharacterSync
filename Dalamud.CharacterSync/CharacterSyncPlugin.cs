using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dalamud.CharacterSync.Interface;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.Logging;
using Dalamud.Plugin;
using Dalamud.RichPresence.Config;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace Dalamud.CharacterSync;

/// <summary>
///     Main plugin class.
/// </summary>
internal class CharacterSyncPlugin : IDalamudPlugin
{
    private readonly WindowSystem windowSystem;
    private readonly ConfigWindow configWindow;
    private readonly WarningWindow warningWindow;

    private readonly bool isSafeMode;

    private readonly Hook<FileInterfaceOpenFileDelegate> openFileHook;
    private readonly string userdataPath;

    private readonly Regex saveFolderRegex = new(
        @"(?<path>.*)FFXIV_CHR(?<cid>.*)\/(?!ITEMODR\.DAT|ITEMFDR\.DAT|GEARSET\.DAT|UISAVE\.DAT|.*\.log)(?<dat>.*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     Initializes a new instance of the <see cref="CharacterSyncPlugin" /> class.
    /// </summary>
    /// <param name="pluginInterface">Dalamud plugin interface.</param>
    public CharacterSyncPlugin(DalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        Service.Configuration = Service.Interface.GetPluginConfig() as CharacterSyncConfig ?? new CharacterSyncConfig();

        this.configWindow = new ConfigWindow();
        this.warningWindow = new WarningWindow();
        this.windowSystem = new WindowSystem("CharacterSync");
        this.windowSystem.AddWindow(this.configWindow);
        this.windowSystem.AddWindow(this.warningWindow);

        Service.Interface.UiBuilder.Draw += this.windowSystem.Draw;
        Service.Interface.UiBuilder.OpenConfigUi += this.OnOpenConfigUi;

        Service.CommandManager.AddHandler("/pcharsync", new CommandInfo(this.OnChatCommand)
        {
            HelpMessage = "Open the Character Sync configuration.",
            ShowInHelp = true
        });

        if (Service.Interface.Reason == PluginLoadReason.Installer)
        {
            PluginLog.Warning("Installer, safe mode...");
            this.isSafeMode = true;
        }
        else if (Service.ClientState.LocalPlayer != null)
        {
            PluginLog.Warning("Boot while logged in, safe mode...");
            this.isSafeMode = true;

            this.warningWindow.IsOpen = true;
        }

        this.userdataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games",
            "FINAL FANTASY XIV - A Realm Reborn");

        try
        {
            this.DoBackup();
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Could not backup character data files.");
        }

        var address = new PluginAddressResolver();
        address.Setup(Service.Scanner);

        this.openFileHook =
            Service.Interop.HookFromAddress<FileInterfaceOpenFileDelegate>(address.FileInterfaceOpenFileAddress,
                this.OpenFileDetour);
        this.openFileHook.Enable();

        Service.ClientState.Login += this.OnLogin;
    }

    private delegate IntPtr FileInterfaceOpenFileDelegate(
        IntPtr pFileInterface,
        [MarshalAs(UnmanagedType.LPWStr)] string filepath, // IntPtr pFilepath
        uint a3);

    /// <inheritdoc />
    public string Name => "Character Sync";

    /// <inheritdoc />
    public void Dispose()
    {
        Service.CommandManager.RemoveHandler("/pcharsync");
        Service.Interface.UiBuilder.Draw -= this.windowSystem.Draw;
        Service.ClientState.Login -= this.OnLogin;
        this.warningWindow?.Dispose();
        this.openFileHook?.Dispose();
    }

    private void OnOpenConfigUi()
    {
        this.configWindow.Toggle();
    }

    private void OnChatCommand(string command, string arguments)
    {
        if (arguments == "fix-gearsets")
            this.FixGearsets();
        else
            this.configWindow.Toggle();
    }

    private void DoBackup()
    {
        var configFolder = Service.Interface.GetPluginConfigDirectory();
        Directory.CreateDirectory(configFolder);

        var backupFolder = new DirectoryInfo(Path.Combine(configFolder, "backups"));
        Directory.CreateDirectory(backupFolder.FullName);

        var folders = backupFolder.GetDirectories().OrderBy(x => long.Parse(x.Name)).ToArray();
        if (folders.Length > 2) folders.FirstOrDefault()?.Delete(true);

        var thisBackupFolder =
            Path.Combine(backupFolder.FullName, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
        Directory.CreateDirectory(thisBackupFolder);

        var xivFolder = new DirectoryInfo(this.userdataPath);

        if (!xivFolder.Exists)
        {
            PluginLog.Error("Could not find XIV folder.");
            return;
        }

        foreach (var directory in xivFolder.GetDirectories("FFXIV_CHR*"))
        {
            var thisBackupFile = Path.Combine(thisBackupFolder, directory.Name);
            PluginLog.Information(thisBackupFile);
            Directory.CreateDirectory(thisBackupFile);

            foreach (var filePath in directory.GetFiles("*.DAT"))
                File.Copy(filePath.FullName, filePath.FullName.Replace(directory.FullName, thisBackupFile), true);
        }

        PluginLog.Information("Backup OK!");
    }

    private IntPtr OpenFileDetour(IntPtr pFileInterface, [MarshalAs(UnmanagedType.LPWStr)] string filepath, uint a3)
    {
        try
        {
            if (Service.Configuration.Cid != 0)
            {
                var match = this.saveFolderRegex.Match(filepath);
                if (match.Success)
                {
                    var rootPath = match.Groups["path"].Value;
                    var datName = match.Groups["dat"].Value;

                    if (this.isSafeMode)
                    {
                        PluginLog.Information($"SAFE MODE: {filepath}");
                    }
                    else if (this.PerformRewrite(datName))
                    {
                        filepath = $"{rootPath}FFXIV_CHR{Service.Configuration.Cid:X16}/{datName}";
                        PluginLog.Information("REWRITE: " + filepath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PluginLog.LogError(ex, "ERROR in OpenFileDetour");
        }

        return this.openFileHook.Original(pFileInterface, filepath, a3);
    }

    private bool PerformRewrite(string datName)
    {
        switch (datName)
        {
            case "HOTBAR.DAT" when Service.Configuration.SyncHotbars:
            case "MACRO.DAT" when Service.Configuration.SyncMacro:
            case "KEYBIND.DAT" when Service.Configuration.SyncKeybind:
            case "LOGFLTR.DAT" when Service.Configuration.SyncLogfilter:
            case "COMMON.DAT" when Service.Configuration.SyncCharSettings:
            case "CONTROL0.DAT" when Service.Configuration.SyncKeyboardSettings:
            case "CONTROL1.DAT" when Service.Configuration.SyncGamepadSettings:
            case "GS.DAT" when Service.Configuration.SyncCardSets:
            case "ADDON.DAT":
                return true;
        }

        return false;
    }

    private void OnLogin()
    {
        var player = Service.ClientState.LocalPlayer!;
        PluginLog.Information($"OnLogin with {player}");
        // TODO: also listen for new gearsets ?
        // this.FixGearsets();
    }

    /// <summary>
    ///     Re-number the gearsets of the current character to match the main character ones.
    /// </summary>
    private void FixGearsets()
    {
        var player = Service.ClientState.LocalPlayer!;
        var mainCid = Service.Configuration.Cid;
        var timings = Stopwatch.StartNew();
        PluginLog.Information("Starting FixGearsets with mainCid={0:X16}", mainCid);

        if (mainCid == 0 || player.NameId == mainCid)
        {
            PluginLog.Information("No main character configured or already logged in, skipping");
            return;
        }

        try
        {
            this.DoFixGearsets(mainCid);
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "Unable to fix gearset numbers");
        }

        PluginLog.Information("FixGearsets done in {0}µs", timings.Elapsed.TotalMicroseconds);
    }

    private unsafe void DoFixGearsets(ulong mainCid)
    {
        var gsModule = Framework.Instance()->GetUiModule()->GetRaptureGearsetModule();
        var mainGearsets = GearsetUtils.ReadGearsets(
            Path.Combine(this.userdataPath, $"FFXIV_CHR{mainCid:X16}", "GEARSET.DAT"));
        PluginLog.Information("Main gearsets: {@0}", mainGearsets);

        var altGearsets = new List<GearsetUtils.GearsetInfo?>();
        foreach (var entry in gsModule->EntriesSpan)
        {
            if (!entry.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            altGearsets.Add(new GearsetUtils.GearsetInfo(entry.ID, entry.ClassJob, entry.CopyName()));
        }

        PluginLog.Information("Pre gearsets: {@0}", altGearsets);

        // var altById = altGearsets.ToDictionary(it => it.id);
        // var altByName = IndexToQueue(altGearsets, it => it.name);
        // var altByJob = IndexToQueue(altGearsets, it => it.jobId);
        void Replace(int altIdx, GearsetUtils.GearsetInfo main)
        {
            var alt = altGearsets[altIdx]!.Value;
            PluginLog.Information("Swapping gearsets {0} and {1}", alt.id, main.id);
            // Remove this gearset from the potential candidates, we won't touch it again
            altGearsets[altIdx] = null;
            if (main.id == alt.id) return;

            gsModule->SwapGearsets(main.id, alt.id);
            // Also update altGearsets with the id change (ignore mainGearsets, we only read each item once)
            var toUpdate = altGearsets.FindIndex(it => it?.id == main.id);
            if (toUpdate != -1) altGearsets[toUpdate] = altGearsets[toUpdate]!.Value with { id = main.id };
        }

        // Scale in O(N²) with the (active) gearsets, but probably still cheaper (and simpler) than allocating indices.
        foreach (var mainGearset in mainGearsets)
        {
            PluginLog.Information("Looking for a match for {0}", mainGearset);
            // var sameNames = altByName[mainGearset.name];
            var sameName = altGearsets.FindIndex(it => it?.name == mainGearset.name);
            if (sameName != -1)
            {
                PluginLog.Information("Found match by name: {0}", altGearsets[sameName]!);
                Replace(sameName, mainGearset);
                continue;
            }

            // var sameJobs = altByJob[mainGearset.jobId];
            var sameJob = altGearsets.FindIndex(it => it?.jobId == mainGearset.jobId);
            if (sameJob != -1)
            {
                PluginLog.Information("Found match by job: {0}", altGearsets[sameJob]!);
                Replace(sameJob, mainGearset);
                continue;
            }

            var compatJob = GetCompatibleJob(mainGearset.jobId);
            if (compatJob != null && (sameJob = altGearsets.FindIndex(it => it?.jobId == compatJob)) != -1)
            {
                PluginLog.Information("Found match by compatible job: {0}", altGearsets[sameJob]!);
                Replace(sameJob, mainGearset);
                continue;
            }

            PluginLog.Information("No match found, ignoring it");
        }

        PluginLog.Information("Post gearsets: {@0}", altGearsets);
    }

    private static byte? GetCompatibleJob(byte job)
    {
        // Jobs 1-7 are GLA, PGL, MRD, LNC, ARC, CNJ and THM, while jobs 19-25 are their high level counterparts.
        return 1 <= job && job <= 7 ? (byte)(job + 18) :
            19 <= job && job <= 25 ? (byte)(job - 18) :
            null;
    }

    // private static IDictionary<TK, Queue<TV>> IndexToQueue<TK, TV>(IEnumerable<TV> items, Func<TV, TK> selector)
    // {
    //     var res = new Dictionary<TK, Queue<TV>>();
    //     foreach (var item in items)
    //     {
    //         var key = selector(item);
    //         if (res.TryGetValue(key, out var queue))
    //             queue.Enqueue(item);
    //         else
    //             res[key] = new Queue<TV>(new[] { item });
    //     }
    //
    //     return res;
    // }
}