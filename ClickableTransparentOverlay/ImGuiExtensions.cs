using System.Collections;
using System.Collections.Generic;
using Hexa.NET.ImGui;

namespace ClickableTransparentOverlay;

public static class ImGuiExtensions
{
    public struct ImVectorEnumerator<T>(ImVector<T> vector) : IEnumerator<T> where T : unmanaged
    {
        private int index = -1;

        public bool MoveNext() => ++index < vector.Size;

        public void Reset()
        {
            index = -1;
        }

        public T Current => vector[index];
        object? IEnumerator.Current => Current;

        public void Dispose() { }
    }

    public static ImVectorEnumerator<T> GetEnumerator<T>(this ImVector<T> vector) where T : unmanaged => new(vector);
}
