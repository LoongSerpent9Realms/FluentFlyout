// Copyright (c) 2024-2026 The PulseFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO;
using Windows.Storage;

namespace FluentFlyoutWPF.Classes.Utils
{
    internal class FileSystemHelper
    {
        public static string GetLogsPath()
        {
            string path;

            // the way MSIX apps choose a logs path work incredibly weirdly (i haven't figured it out), so we're searching multiple possible locations
            // first, check same path as where settings is saved
            try
            {
                path = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path,
                    "Roaming",
                    "PulseFlyout");
                if (Directory.Exists(path))
                    return path;
            }
            catch { }

            // if that doesn't work, check %appData%\PulseFlyout
            try
            {
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PulseFlyout");
                if (Directory.Exists(path))
                    return path;
            }
            catch { }

            // if neither of those exist, return hardcoded path
            // %localAppData%\Packages\unchihugo.PulseFlyout_69b7b6qge1ahj\LocalCache\Roaming\PulseFlyout
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                "unchihugo.PulseFlyout_69b7b6qge1ahj",
                "LocalCache",
                "Roaming",
                "PulseFlyout"
            );
        }
    }
}
