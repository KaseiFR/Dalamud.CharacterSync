using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.Interop.Attributes;

[assembly: InternalsVisibleTo("Tests")]

namespace Dalamud.CharacterSync;

/// <summary>
///     Utility class for reading GEARSET.DAT files.
/// </summary>
internal static class GearsetUtils
{
    internal record struct GearsetInfo(byte id, byte jobId, string name)
    {
        public override string ToString()
        {
            // var prettyName = Encoding.UTF8.GetString(this.name);
            var prettyName = this.name;
            return $"Gearset #{this.id} '{prettyName}' ({this.jobId})";
        }
    }

    internal static IList<GearsetInfo> ReadGearsets(string gearsetsPath)
    {
        try
        {
            using var file = File.Open(gearsetsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadGearsets(file);
        }
        catch (Exception ex)
        {
            throw new Exception($"Unable to read gearset file {gearsetsPath}", ex);
        }
    }

    internal static unsafe IList<GearsetInfo> ReadGearsets(Stream gearsetsFile)
    {
        var result = new List<GearsetInfo>();
        /*
         * The GEARSET.DAT file has a 21 (!) byte header, the first element being a magic/version word.
         * Then there are the actual gearset entries (having the same layout as RaptureGearsetModule entries).
         * And some remaining data/footer which we don't care about.
         */
        {
            // Check the header magic
            var magic = new byte[4];
            gearsetsFile.ReadExactly(magic);
            if (!magic.SequenceEqual(GEARSET_FILE_MAGIC))
                throw new Exception($"Unsupported magic: {magic}");
            // Skip the rest of the header
            gearsetsFile.Seek(21, SeekOrigin.Begin);

            // Read gearsets
            var buffer = new byte[sizeof(RaptureGearsetModule.GearsetEntry)]; // 448 bytes as of 6.5
            for (var i = 0; i < GEARSET_MAX_COUNT; i += 1)
            {
                gearsetsFile.ReadExactly(buffer);
                // XOR the whole entry with 0x73, which is required for some reason. Hopefully this gets vectorized.
                for (var j = 0; j < buffer.Length; j += 1) buffer[j] ^= 0x73;

                fixed (byte* bufferPtr = buffer)
                {
                    var entry = Unsafe.AsRef<RaptureGearsetModule.GearsetEntry>(bufferPtr);
                    if ((entry.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0) continue; // Invalid entry
                    result.Add(new GearsetInfo(
                        entry.ID,
                        entry.ClassJob,
                        entry.CopyName()
                    ));
                }
            }
        }

        return result;
    }

    // TODO: use the native game gearset swapping feature
    internal static unsafe void SwapGearsets(this RaptureGearsetModule gsModule, int idA, int idB)
    {
        if (idA == idB) return;
        var aPtr = gsModule.GetGearset(idA);
        var bPtr = gsModule.GetGearset(idB);
        (*aPtr, *bPtr) = (*bPtr, *aPtr);
        // var tmp = *aPtr;
        // *aPtr = *bPtr;
        // *bPtr = tmp;
        aPtr->ID = (byte)idB;
        bPtr->ID = (byte)idA;
    }

    // The game seems to use UTF-8 (limited to 3-byte sequences, no emojis) for a subset of Unicode, including
    // some private codepoints for custom game glyphes. We don't really need to decode the strings though, since we
    // only want to compare them.
    internal static unsafe string CopyName(this RaptureGearsetModule.GearsetEntry gearset)
    {
        // Find the end of the string, strlen-style
        var start = gearset.Name;
        var maxPtr = start + GEARSET_NAME_MAXLEN;
        var cur = start;
        while (cur < maxPtr && *cur != 0) cur += 1;
        // Copy from the temporary buffer into an owned string
        return Marshal.PtrToStringUTF8((IntPtr)start, (int)(cur - start));
    }

    // The game seems to use UTF-8 (limited to 3-byte sequences, no emojis) for a subset of Unicode, including
    // some private codepoints for custom game glyphes. We don't really need to decode the strings though, since we
    // only want to compare them.
    internal static unsafe byte[] CopyNameBytes(this RaptureGearsetModule.GearsetEntry gearset)
    {
        // Find the end of the string, strlen-style
        var start = gearset.Name;
        var maxPtr = start + GEARSET_NAME_MAXLEN;
        var cur = start;
        while (cur < maxPtr && *cur != 0) cur += 1;
        // Copy from the temporary buffer into an owned array
        var bytes = new byte[cur - start];
        Marshal.Copy((IntPtr)start, bytes, 0, bytes.Length);
        return bytes;
    }

    internal static readonly int GEARSET_MAX_COUNT = // 100 as of 6.5
        typeof(RaptureGearsetModule).GetField("Entries")!
            .GetCustomAttribute<FixedSizeArrayAttribute<RaptureGearsetModule.GearsetEntry>>()!.Count;

    private static readonly byte[] GEARSET_FILE_MAGIC = { 0x05, 0x00, 0x6C, 0x00 }; // as of 6.5

    private static readonly int GEARSET_NAME_MAXLEN = // 48 as of 6.5
        Marshal.SizeOf(typeof(RaptureGearsetModule.GearsetEntry).GetField("Name")!.FieldType);
}