using System.Buffers;
using Hexa.NET.ImGui;
using Hexa.NET.ImGui.Backends.D3D11;

namespace ClickableTransparentOverlay
{
    using Vortice.DXGI;
    using Vortice.Direct3D;
    using Vortice.Direct3D11;
    using System.Numerics;
    using System.Collections.Generic;
    using System;
    using System.Linq;

    unsafe internal sealed class ImGuiRenderer : IDisposable
    {
        ID3D11Device device;
        ID3D11DeviceContext deviceContext;
        readonly Dictionary<ImTextureID, ID3D11ShaderResourceView> textureResources = new();

        public ImGuiRenderer(ImGuiContextPtr ctx, ID3D11Device device, ID3D11DeviceContext deviceContext, int width, int height)
        {
            this.device = device;
            this.deviceContext = deviceContext;

            device.AddRef();
            deviceContext.AddRef();
            
            ImGuiImplD3D11.SetCurrentContext(ctx);
            ImGuiImplD3D11.Init(new((Hexa.NET.ImGui.Backends.D3D11.ID3D11Device*)device.NativePointer),
                new((Hexa.NET.ImGui.Backends.D3D11.ID3D11DeviceContext*)deviceContext.NativePointer));
            
            ImGui.StyleColorsDark();
            Resize(width, height);
        }

        public void Update(float deltaTime, Action DoRender)
        {
            var io = ImGui.GetIO();
            io.DeltaTime = deltaTime;
            ImGuiImplD3D11.NewFrame();
            ImGui.NewFrame();
            DoRender?.Invoke();
            ImGui.Render();
        }

        public void Render()
        {
            ImGuiImplD3D11.RenderDrawData(ImGui.GetDrawData());
            
            var io = ImGui.GetIO();
            if ((io.ConfigFlags & ImGuiConfigFlags.ViewportsEnable) != 0)
            {
                ImGui.UpdatePlatformWindows();
                ImGui.RenderPlatformWindowsDefault();
            }
        }

        public void Dispose()
        {
            if (device == null)
                return;

            this.DeRegisterAllTexture();
            deviceContext.Release();
            device.Release();
            
            ImGuiImplD3D11.Shutdown();
            ImGuiImplD3D11.SetCurrentContext(ImGuiContextPtr.Null);

            device = null;
        }

        public void Resize(int width, int height)
        {
            ImGui.GetIO().DisplaySize = new Vector2(width, height);
        }

        public ImTextureID CreateImageTexture<T>(Memory<T> memory, int width, int height, Format format) where T : unmanaged
        {
            var texDesc = new Texture2DDescription(format, width, height, 1, 1);

            using MemoryHandle imageMemoryHandle = memory.Pin();
            var subResource = new SubresourceData(imageMemoryHandle.Pointer, texDesc.Width * 4);
            using var texture = device.CreateTexture2D(texDesc, new[] { subResource });
            var resViewDesc = new ShaderResourceViewDescription(texture, ShaderResourceViewDimension.Texture2D, format, 0, texDesc.MipLevels);
            return RegisterTexture(device.CreateShaderResourceView(texture, resViewDesc));
        }

        public bool RemoveImageTexture(IntPtr handle)
        {
            using var tex = this.DeRegisterTexture(handle);
            return tex != null;
        }

        public void UpdateFontTexture(FontHelper.FontLoadDelegate fontLoadFunc)
        {
            var io = ImGui.GetIO();
            io.Fonts.Clear();
            var config = ImGui.ImFontConfig();
            fontLoadFunc(config);
            io.FontDefault = null;
            config.Destroy();
        }

        ImTextureID RegisterTexture(ID3D11ShaderResourceView texture)
        {
            var imguiID = texture.NativePointer;
            textureResources.TryAdd(imguiID, texture);
            return imguiID;
        }

        ID3D11ShaderResourceView? DeRegisterTexture(ImTextureID texturePtr)
        {
            if (textureResources.Remove(texturePtr, out var texture))
            {
                return texture;
            }

            return null;
        }

        void DeRegisterAllTexture()
        {
            foreach (var key in textureResources.Keys.ToArray())
            {
                DeRegisterTexture(key)?.Release();
            }
        }
    }

}
