namespace ClickableTransparentOverlay.Win32
{
    using System;
    using System.Diagnostics;

    public static class Utils
    {
        private static readonly Stopwatch sw = Stopwatch.StartNew();
        private static readonly long[] nVirtKeyTimeouts = new long[256]; // Total VirtKeys are 256.

        /// <summary>
        /// Returns true if the key is pressed.
        /// For keycode information visit: https://www.pinvoke.net/default.aspx/user32.getkeystate.
        ///
        /// This function can return True multiple times (in multiple calls) per keypress. It
        /// depends on how long the application user pressed the key for and how many times
        /// caller called this function while the key was pressed. Caller of this function is
        /// responsible to mitigate this behaviour.
        /// </summary>
        /// <param name="nVirtKey">key code to look.</param>
        /// <returns>weather the key is pressed or not.</returns>
        public static bool IsKeyPressed(VK nVirtKey)
        {
            return Convert.ToBoolean(User32.GetKeyState(nVirtKey) & 0x8000);
        }

        /// <summary>
        /// A wrapper function around <see cref="IsKeyPressed"/> to ensure a single key-press
        /// yield single true even if the function is called multiple times.
        ///
        /// This function might miss a key-press, which may degrade the user-experience,
        /// so use this function to the minimum e.g. just to enable/disable/show/hide the overlay.
        /// And, it would be nice to allow application user to configure the timeout value to
        /// their liking.
        /// </summary>
        /// <param name="nVirtKey">key to look for, for details read <see cref="IsKeyPressed"/> description.</param>
        /// <param name="timeout">timeout in milliseconds</param>
        /// <returns>true if the key is pressed and key is not in timeout.</returns>
        public static bool IsKeyPressedAndNotTimeout(VK nVirtKey, int timeout = 200)
        {
            var actual = IsKeyPressed(nVirtKey);
            var currTime = sw.ElapsedMilliseconds;
            if (actual && currTime > nVirtKeyTimeouts[(int)nVirtKey])
            {
                nVirtKeyTimeouts[(int)nVirtKey] = currTime + timeout;
                return true;
            }

            return false;
        }
    }
}
