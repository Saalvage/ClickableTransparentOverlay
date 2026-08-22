using ClickableTransparentOverlay.Backends.Windows;
using Hexa.NET.ImGui;

namespace SimpleExample
{
    using ClickableTransparentOverlay;

    internal class SampleOverlay : Overlay<WindowsBackend>
    {
        private bool wantKeepDemoWindow = true;
        private int FPSHelper;

        public SampleOverlay()
        {
            this.FPSHelper = this.FPSLimit;
        }

        protected override void Render()
        {
            ImGui.ShowDemoWindow(ref wantKeepDemoWindow);

            if (ImGui.Begin("FPS Changer"))
            {
                if (ImGui.InputInt("Set FPS", ref FPSHelper))
                {
                    this.FPSLimit = this.FPSHelper;
                }
            }

            ImGui.End();

            if (!this.wantKeepDemoWindow)
            {
                this.Close();
            }
        }
    }
}
