using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TerraFX.Interop.Windows;

namespace ARealmRecorded;

public static class Utility
{
    public static unsafe void RequestFileCompression(string filePath)
    {
        try
        {
            using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            var format = TerraFX.Interop.Windows.Windows.COMPRESSION_FORMAT_DEFAULT;
            TerraFX.Interop.Windows.Windows.DeviceIoControl((HANDLE)handle.DangerousGetHandle(), FSCTL.FSCTL_SET_COMPRESSION, &format, sizeof(ushort), null, 0, null, null);
        }
        catch(Exception ex)
        {
            DalamudApi.LogError(ex.ToString());
        }
    }

    public static unsafe bool IsOnCompressibleNtfsDrive(string filePath)
    {
        char* volumeNameBuffer = null;
        char* fsNameBuffer = null;
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var root = Path.GetPathRoot(fullPath);

            if(string.IsNullOrEmpty(root))
            {
                return false;
            }

            var bufferSize = 32768u;
            volumeNameBuffer = (char*)NativeMemory.AllocZeroed(bufferSize);
            fsNameBuffer = (char*)NativeMemory.AllocZeroed(bufferSize);

            uint serialNumber;
            uint maxComponentLength;
            uint flags;

            fixed(char* rootPtr = root)
            {
                var result = TerraFX.Interop.Windows.Windows.GetVolumeInformationW(rootPtr, volumeNameBuffer, bufferSize, &serialNumber, &maxComponentLength, &flags, fsNameBuffer, bufferSize);

                if(result == TerraFX.Interop.Windows.Windows.FALSE)
                {
                    return false;
                }
            }

            var fsName = new string(fsNameBuffer);

            var isNtfs = fsName.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            var supportsComp = (flags & FILE.FILE_FILE_COMPRESSION) != 0;

            return isNtfs && supportsComp;
        }
        catch(Exception ex)
        {
            DalamudApi.LogError(ex.ToString());
            return false;
        }
        finally
        {
            if(volumeNameBuffer != null)
            {
                NativeMemory.Free(volumeNameBuffer);
            }

            if(fsNameBuffer != null)
            {
                NativeMemory.Free(fsNameBuffer);
            }
        }
    }
}
