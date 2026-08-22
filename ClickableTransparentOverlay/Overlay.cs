using ClickableTransparentOverlay.Win32;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.Win32;

namespace ClickableTransparentOverlay
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Vortice.Direct3D;
    using Vortice.Direct3D11;
    using Vortice.DXGI;
    using Vortice.Mathematics;
    using System.Collections.Concurrent;

    /// <summary>
    /// A class to create clickable transparent overlay on windows machine.
    /// </summary>
    public abstract class Overlay : IDisposable
    {
        private readonly string title;
        private readonly Format format;

        private WNDCLASSEX wndClass;

        /// <summary>
        ///  Do not assume this class is initialized.
        ///  Consider using this variable only in <see cref="PostInitialized"/> or <see cref="Render"/> function.
        /// </summary>
        public Win32Window window;
        private ID3D11Device device;
        private ID3D11DeviceContext deviceContext;

        private ImGuiRenderer renderer;

        private bool _disposedValue;
        private IntPtr selfPointer;
        private Thread renderThread;
        private volatile CancellationTokenSource cancellationTokenSource;
        private volatile bool overlayIsReady;

        private Dictionary<string, (IntPtr Handle, uint Width, uint Height)> loadedTexturesPtrs;

        private readonly ConcurrentQueue<FontHelper.FontLoadDelegate> fontUpdates;

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="Overlay"/> class.
        /// </summary>
        /// <param name="windowTitle">
        /// Title of the window created by the overlay
        /// </param>
        /// <param name="DPIAware">
        /// should the overlay scale with windows scale value or not.
        /// </param>
        public Overlay(string windowTitle = "Overlay", bool DPIAware = false)
        {
            this.VSync = false;
            this.FPSLimit = 60;
            this._disposedValue = false;
            this.overlayIsReady = false;
            this.title = windowTitle;
            this.cancellationTokenSource = new();
            this.format = Format.R8G8B8A8_UNorm;
            this.loadedTexturesPtrs = new();
            this.fontUpdates = new();
            if (DPIAware)
            {
                User32.SetProcessDPIAware();
            }
        }

        #endregion

        #region PublicAPI

        /// <summary>
        /// Starts the overlay
        /// </summary>
        /// <returns>A Task that finishes once the overlay window is ready</returns>
        public async Task Start()
        {
            this.renderThread = new Thread(async () =>
            {
                await this.InitializeResources();
                this.ReplaceFontIfRequired();
                this.RunInfiniteLoop(this.cancellationTokenSource.Token);
            });

            this.renderThread.Start();
            await WaitHelpers.SpinWait(() => this.overlayIsReady);
        }

        /// <summary>
        /// Starts the overlay and waits for the overlay window to be closed.
        /// </summary>
        /// <returns>A task that finishes once the overlay window closes</returns>
        public virtual async Task Run()
        {
            if (!this.overlayIsReady)
            {
                await this.Start();
            }

            await WaitHelpers.SpinWait(() => this.cancellationTokenSource.IsCancellationRequested);
        }

        /// <summary>
        /// Safely Closes the Overlay.
        /// </summary>
        public virtual void Close()
        {
            this.cancellationTokenSource.Cancel();
        }

        /// <summary>
        /// Safely dispose all the resources created by the overlay
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Replaces the ImGui font with another one.
        /// </summary>
        /// <param name="pathName">pathname to the TTF font file.</param>
        /// <param name="size">font size to load.</param>
        /// <param name="language">supported language by the font.</param>
        /// <returns>true if the font replacement is valid otherwise false.</returns>
        public unsafe bool ReplaceFont(string pathName, float size)
        {
            if (!File.Exists(pathName))
            {
                return false;
            }

            this.fontUpdates.Enqueue(config =>
            {
                var io = ImGui.GetIO();
                io.Fonts.AddFontFromFileTTF(pathName, size, config);
                ImGui.GetIO().FontDefault = null;
            });

            return true;
        }

        /// <summary>
        /// Replaces the ImGui font with another one.
        /// </summary>
        /// <param name="pathName">pathname to the TTF font file.</param>
        /// <param name="size">font size to load.</param>
        /// <param name="glyphRange">custom glyph range of the font to load. Read <see cref="FontGlyphRangeType"/> for more detail.</param>
        /// <returns>>true if the font replacement is valid otherwise false.</returns>
        public unsafe bool ReplaceFont(string pathName, float size, uint[] glyphRange)
        {
            if (!File.Exists(pathName))
            {
                return false;
            }

            this.fontUpdates.Enqueue(config =>
            {
                var io = ImGui.GetIO();
                io.Fonts.AddFontFromFileTTF(pathName, size, config, glyphRange[0]);
                ImGui.GetIO().FontDefault = null;
            });

            return true;
        }

        /// <summary>
        /// Replaces the ImGui font with the default ImGui font.
        /// </summary>
        /// <returns>always return true</returns>
        public unsafe bool ReplaceFont()
        {
            this.fontUpdates.Enqueue(config =>
            {
                var io = ImGui.GetIO();
                io.Fonts.AddFontDefault(config);
                ImGui.GetIO().FontDefault = null;
            });

            return true;
        }

        /// <summary>
        /// Replaces the ImGui font with another one.
        /// </summary>
        /// <param name="fontLoadDelegate">instructions for loading the font</param>
        public unsafe bool ReplaceFont(FontHelper.FontLoadDelegate fontLoadDelegate)
        {
            // have to do this because of issue: https://github.com/ocornut/imgui/issues/6858
            ImGui.GetIO().FontDefault = null;
            this.fontUpdates.Enqueue(fontLoadDelegate);
            return true;
        }

        /// <summary>
        /// Enable or disable the vsync on the overlay.
        /// You can either use the <see cref="FPSLimit"/> or <see cref="VSync"/>.
        /// VSync will be given the preference if both are set.
        /// </summary>
        public bool VSync;

        /// <summary>
        /// Gets or sets the FPS Limits of the overlay.
        /// You can either use the <see cref="FPSLimit"/> or <see cref="VSync"/>.
        /// VSync will be given the preference if both are set.
        /// </summary>
        public int FPSLimit
        {
            get;
            set {
                if (value == 0)
                {
                    field = value;
                    _ = Winmm.MM_EndPeriod(1);
                }
                else if (value > 0)
                {
                    field = value;
                    _ = Winmm.MM_BeginPeriod(1);
                }
                else
                {
                    // ignore negative values.
                }
            }
        }

        /// <summary>
        /// Adds the image to the Graphic Device as a texture.
        /// Then returns the pointer of the added texture. It also
        /// cache the image internally rather than creating a new texture on every call,
        /// so this function can be called multiple times per frame.
        /// </summary>
        /// <param name="name">user friendly name given to the image.</param>
        /// <param name="memory">Raw image data in the specific format.</param>
        /// <param name="width">Image width.</param>
        /// <param name="height">Image height.</param>
        /// <param name="format">Format of the image data.</param>
        /// <param name="handle">output pointer to the image in the graphic device.</param>
        public unsafe void AddOrGetImagePointer<T>(string name, Memory<T> memory, int width, int height, Format format,
            out ImTextureRef handle) where T : unmanaged
        {
            ImTextureID id;
            if (this.loadedTexturesPtrs.TryGetValue(name, out var data))
            {
                id = data.Handle;
            }
            else
            {
                id = this.renderer.CreateImageTexture(memory, width, height, format);
                this.loadedTexturesPtrs.Add(name, new(id, (uint)width, (uint)height));
            }
            handle = new(null, id);
        }

        /// <summary>
        /// Removes the image from the Overlay.
        /// </summary>
        /// <param name="key">name or pathname which was used to add the image in the first place.</param>
        /// <returns> true if the image is removed otherwise false.</returns>
        public bool RemoveImage(string key)
        {
            if (this.loadedTexturesPtrs.Remove(key, out var data))
            {
                return this.renderer.RemoveImageTexture(data.Handle);
            }

            return false;
        }

        #endregion

        protected virtual void Dispose(bool disposing)
        {
            if (this._disposedValue)
            {
                return;
            }

            if (disposing)
            {
                if (this.FPSLimit > 0)
                {
                    Winmm.MM_EndPeriod(1);
                }

                this.renderThread?.Join();
                foreach(var key in this.loadedTexturesPtrs.Keys.ToArray())
                {
                    this.RemoveImage(key);
                }

                this.cancellationTokenSource?.Dispose();
                this.fontUpdates?.Clear();
                this.renderer?.Dispose();
                this.window?.Dispose();
                this.deviceContext?.Release();
                this.device?.Release();
                
                ImGuiImplWin32.Shutdown();
                ImGuiImplWin32.SetCurrentContext(ImGuiContextPtr.Null);
                ImGui.DestroyContext();
            }

            if (this.selfPointer != IntPtr.Zero)
            {
                if (!User32.UnregisterClass(this.title, this.selfPointer))
                {
                    throw new Exception($"Failed to Unregister {this.title} class during dispose.");
                }

                this.selfPointer = IntPtr.Zero;
            }

            this._disposedValue = true;
        }

        /// <summary>
        /// Steps to execute after the overlay has fully initialized.
        /// </summary>
        protected virtual Task PostInitialized()
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Abstract Task for creating the UI.
        /// </summary>
        /// <returns>Task that finishes once per frame</returns>
        protected abstract void Render();

        private void RunInfiniteLoop(CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            var currentTimeSec = 0f;
            var clearColor = new Color4(0.0f);
            var delayMs = 0f;
            var sleepTimeMs = 0;
            while (!token.IsCancellationRequested)
            {
                currentTimeSec = stopwatch.ElapsedTicks / (float)Stopwatch.Frequency;
                stopwatch.Restart();
                this.window.PumpEvents();
                ImGuiImplWin32.NewFrame();
                this.renderer.Update(currentTimeSec, Render);
                this.renderer.Render();
                if (this.FPSLimit > 0)
                {
                    delayMs = 1000f / this.FPSLimit;
                    currentTimeSec = stopwatch.ElapsedTicks / (float)Stopwatch.Frequency;
                    sleepTimeMs = (int)(delayMs - (currentTimeSec * 1000));
                    if (sleepTimeMs > 0)
                    {
                        Thread.Sleep(sleepTimeMs);
                    }
                }

                this.ReplaceFontIfRequired();
            }
        }

        private void ReplaceFontIfRequired()
        {
            if (this.renderer != null)
            {
                while (this.fontUpdates.TryDequeue(out var update))
                {
                    this.renderer.UpdateFontTexture(update);
                }
            }
        }

        private async Task InitializeResources()
        {
            D3D11.D3D11CreateDevice(
                null,
                DriverType.Hardware,
                DeviceCreationFlags.None,
                new[] { FeatureLevel.Level_10_0 },
                out device,
                out deviceContext);
            
            selfPointer = Kernel32.GetModuleHandle(null);
            wndClass = new WNDCLASSEX
            {
                Size = Unsafe.SizeOf<WNDCLASSEX>(),
                Styles = WindowClassStyles.CS_HREDRAW | WindowClassStyles.CS_VREDRAW | WindowClassStyles.CS_PARENTDC,
                WindowProc = WndProc,
                InstanceHandle = this.selfPointer,
                CursorHandle = User32.LoadCursor(IntPtr.Zero, SystemCursor.IDC_ARROW),
                BackgroundBrushHandle = IntPtr.Zero,
                IconHandle = IntPtr.Zero,
                MenuName = string.Empty,
                ClassName = this.title,
                SmallIconHandle= IntPtr.Zero,
                ClassExtraBytes = 0,
                WindowExtraBytes = 0
            };

            if (User32.RegisterClassEx(ref wndClass) == 0)
            {
                throw new Exception($"Failed to Register class of name {wndClass.ClassName}");
            }

            window = new Win32Window(wndClass.ClassName, 1, 1, 0, 0, title,
                0, WindowExStyles.WS_EX_TOOLWINDOW);
            
            var ctx = ImGui.CreateContext();
            
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
            io.ConfigViewportsNoAutoMerge = true;
            
            ImGuiImplWin32.SetCurrentContext(ctx);
            ImGuiImplWin32.Init(window.Handle);
            
            renderer = new ImGuiRenderer(ctx, device, deviceContext, 0, 0);
            overlayIsReady = true;
            await PostInitialized();
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
        {
            if (this.overlayIsReady)
            {
                if (ImGuiImplWin32.WndProcHandler(hWnd, msg, wParam, lParam) != 0)
                {
                    return IntPtr.Zero;
                }
            }

            return User32.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }
}
