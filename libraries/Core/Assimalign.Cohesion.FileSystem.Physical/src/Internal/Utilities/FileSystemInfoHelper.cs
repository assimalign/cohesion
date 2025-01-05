using System;
using System.Diagnostics;
using System.IO;

namespace Assimalign.Cohesion.FileSystem.Internal;


internal static class FileSystemInfoHelper
{
    public static bool IsExcluded(FileSystemInfo fileSystemInfo, ExclusionFilterType filters)
    {
        if (filters == ExclusionFilterType.None)
        {
            return false;
        }
        else if (fileSystemInfo.Name.StartsWith(".", StringComparison.Ordinal) && (filters & ExclusionFilterType.DotPrefixed) != 0)
        {
            return true;
        }
        else if (fileSystemInfo.Exists &&
            (((fileSystemInfo.Attributes & FileAttributes.Hidden) != 0 && (filters & ExclusionFilterType.Hidden) != 0) ||
             ((fileSystemInfo.Attributes & FileAttributes.System) != 0 && (filters & ExclusionFilterType.System) != 0)))
        {
            return true;
        }

        return false;
    }

    public static DateTime? GetFileLinkTargetLastWriteTimeUtc(string filePath)
    {
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Exists)
        {
            return GetFileLinkTargetLastWriteTimeUtc(fileInfo);
        }

        return null;
    }

    // If file is a link and link target exists, return target's LastWriteTimeUtc.
    // If file is a link, and link target does not exists, return DateTime.MinValue
    //   since the link's LastWriteTimeUtc doesn't convey anything for this scenario.
    // If file is not a link, return null to inform the caller that file is not a link.
    public static DateTime? GetFileLinkTargetLastWriteTimeUtc(FileInfo fileInfo)
    {
        Debug.Assert(fileInfo.Exists);
        if (fileInfo.LinkTarget != null)
        {
            try
            {
                FileSystemInfo targetInfo = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (targetInfo != null && targetInfo.Exists)
                {
                    return targetInfo.LastWriteTimeUtc;
                }
            }
            catch (FileNotFoundException)
            {
                // The file ceased to exist between LinkTarget and ResolveLinkTarget.
            }

            return DateTime.MinValue;
        }

        return null;
    }
}

