/*
	Copyright (C) 2026 Llamasoft

	This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

	This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.

	You should have received a copy of the GNU General Public License along with this program. If not, see <https://www.gnu.org/licenses/>. 
*/

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EdgeLoop.Classes
{
    /// <summary>
    /// Handles power management to prevent the monitor from turning off during playback
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class PowerManagement
    {
        [Flags]
        public enum EXECUTION_STATE : uint
        {
            ES_AWAYMODE_REQUIRED = 0x00000040,
            ES_CONTINUOUS = 0x80000000,
            ES_DISPLAY_REQUIRED = 0x00000002,
            ES_SYSTEM_REQUIRED = 0x00000001
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

        private static bool _isSuppressingSleep = false;

        /// <summary>
        /// Prevents the system from entering sleep mode and keeps the display on
        /// </summary>
        public static void SuppressSleep()
        {
            if (_isSuppressingSleep) return;

            try
            {
                Logger.Debug("[PowerManagement] Suppressing sleep and display-off to maintain playback.");
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_DISPLAY_REQUIRED | EXECUTION_STATE.ES_SYSTEM_REQUIRED);
                _isSuppressingSleep = true;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to suppress sleep", ex);
            }
        }

        /// <summary>
        /// Allows the system to enter sleep mode and turn off the display normally
        /// </summary>
        public static void AllowSleep()
        {
            if (!_isSuppressingSleep) return;

            try
            {
                Logger.Debug("[PowerManagement] Releasing sleep suppression.");
                SetThreadExecutionState(EXECUTION_STATE.ES_CONTINUOUS);
                _isSuppressingSleep = false;
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to release sleep suppression", ex);
            }
        }
    }
}
