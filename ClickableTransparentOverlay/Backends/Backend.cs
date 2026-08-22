using System;
using Hexa.NET.ImGui;

namespace ClickableTransparentOverlay.Backends;

public interface IBackend : IDisposable
{
    static abstract IBackend Create(ImGuiContextPtr ctx, string windowTitle);

    ImTextureID LoadTexture<T>(Memory<T> memory, int width, int height, uint format);
    void FreeTexture(ImTextureID texture);
    
    void BeginRender();
    void EndRender();
}
