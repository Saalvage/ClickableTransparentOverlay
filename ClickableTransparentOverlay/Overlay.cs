using ClickableTransparentOverlay.Backends;
using Hexa.NET.ImGui;

namespace ClickableTransparentOverlay
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Collections.Concurrent;

    /// <summary>
    /// A class to create a multi-viewport ImGui application without a main window.
    /// </summary>
    public abstract class Overlay<T> : IDisposable where T : IBackend
    {
        private bool _disposed;
        
        private Task _runTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly TaskCompletionSource _ready = new();
        
        private readonly string _title;
        
        private IBackend _backend;

        private readonly Dictionary<string, ImTextureID> _loadedTextures = [];
        private readonly ConcurrentQueue<Action<ImFontConfigPtr>> _fontUpdates = [];

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="Overlay"/> class.
        /// </summary>
        /// <param name="windowTitle">
        /// Title of the window created by the overlay
        /// </param>
        public Overlay(string windowTitle = "Overlay")
        {
            FPSLimit = 60;
            _disposed = false;
            _title = windowTitle;
        }

        #endregion

        #region PublicAPI

        /// <summary>
        /// Starts the overlay
        /// </summary>
        /// <returns>A Task that finishes once the overlay window is ready</returns>
        public async Task Start()
        {
            _runTask = Task.Run(() =>
            {
                InitializeResources();
                ReplaceFontIfRequired();
                RunInfiniteLoop(_cancellationTokenSource.Token);
            });

            await _ready.Task;
        }

        /// <summary>
        /// Starts the overlay and waits for the overlay window to be closed.
        /// </summary>
        /// <returns>A task that finishes once the overlay window closes</returns>
        public virtual async Task Run()
        {
            if (!_ready.Task.IsCompleted)
            {
                await Start();
            }

            await _runTask;
        }

        /// <summary>
        /// Safely closes the overlay.
        /// </summary>
        public virtual void Close()
        {
            _cancellationTokenSource.Cancel();
        }

        ~Overlay()
        {
            Dispose(false);
        }
        
        /// <summary>
        /// Safely dispose all the resources created by the overlay
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Replaces the ImGui font with another one.
        /// </summary>
        /// <param name="pathName">pathname to the TTF font file.</param>
        /// <param name="size">font size to load.</param>
        /// <returns>true if the font replacement is valid otherwise false.</returns>
        public unsafe bool ReplaceFont(string pathName, float size)
        {
            if (!File.Exists(pathName))
            {
                return false;
            }

            _fontUpdates.Enqueue(config =>
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

            _fontUpdates.Enqueue(config =>
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
            _fontUpdates.Enqueue(config =>
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
        public unsafe bool ReplaceFont(Action<ImFontConfigPtr> fontLoadDelegate)
        {
            // have to do this because of issue: https://github.com/ocornut/imgui/issues/6858
            ImGui.GetIO().FontDefault = null;
            _fontUpdates.Enqueue(fontLoadDelegate);
            return true;
        }

        /// <summary>
        /// Gets or sets the FPS limits of the overlay.
        /// </summary>
        public int FPSLimit
        {
            get;
            set {
                if (value >= 0)
                {
                    field = value;
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
        public unsafe void AddOrGetImagePointer<T>(string name, Memory<T> memory, int width, int height, uint format,
            out ImTextureRef handle) where T : unmanaged
        {
            if (!_loadedTextures.TryGetValue(name, out var id))
            {
                id = _backend.LoadTexture(memory, width, height, format);
                _loadedTextures.Add(name, id);
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
            if (_loadedTextures.Remove(key, out var data))
            {
                _backend.FreeTexture(data);
                return true;
            }

            return false;
        }

        #endregion

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            _cancellationTokenSource.Cancel();
            _runTask.Wait();
            
            foreach (var (_, tex) in _loadedTextures)
            {
                _backend.FreeTexture(tex);
            }
            _loadedTextures.Clear();

            _backend.Dispose();
            
            ImGui.DestroyContext();

            if (disposing)
            {
                _fontUpdates.Clear();
            }
        }

        /// <summary>
        /// Steps to execute after the overlay has fully initialized.
        /// </summary>
        protected virtual void PostInitialized()
        {
        }

        /// <summary>
        /// Abstract Task for creating the UI.
        /// </summary>
        /// <returns>Task that finishes once per frame</returns>
        protected abstract void Render();

        private void RunInfiniteLoop(CancellationToken cancellationToken)
        {
            var now = Stopwatch.GetTimestamp();
            while (!cancellationToken.IsCancellationRequested)
            {
                var prev = now;
                now = Stopwatch.GetTimestamp();
                var deltaTime = Stopwatch.GetElapsedTime(prev, now);
                var io = ImGui.GetIO();
                io.DeltaTime = (float)deltaTime.TotalSeconds;
                _backend.BeginRender();
                ImGui.NewFrame();
                Render();
                ImGui.Render();
                _backend.EndRender();
                if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
                {
                    ImGui.UpdatePlatformWindows();
                    ImGui.RenderPlatformWindowsDefault();
                }
                
                if (FPSLimit > 0)
                {
                    var timePerFrameTarget = TimeSpan.FromSeconds(1) / FPSLimit;
                    var sleep = timePerFrameTarget - deltaTime;
                    if (sleep > TimeSpan.Zero)
                    {
                        Thread.Sleep(sleep);
                    }
                }

                ReplaceFontIfRequired();
            }
        }

        private void ReplaceFontIfRequired()
        {
            while (_fontUpdates.TryDequeue(out var update))
            {
                var io = ImGui.GetIO();
                io.Fonts.Clear();
                var config = ImGui.ImFontConfig();
                update(config);
                io.FontDefault = null;
                config.Destroy();
            }
        }

        private void InitializeResources()
        {
            var ctx = ImGui.CreateContext();
            
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
            io.ConfigFlags |= ImGuiConfigFlags.ViewportsEnable;
            io.ConfigViewportsNoAutoMerge = true;
            
            _backend = T.Create(ctx, _title);

            _ready.SetResult();
            PostInitialized();
        }
    }
}
