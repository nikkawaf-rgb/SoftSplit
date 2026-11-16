using System;
using System.Text;
using SharpDX.Direct3D;
using SharpDX.DXGI;
using D3D11Device = SharpDX.Direct3D11.Device;

namespace SoftSplit.Core
{
    /// <summary>Probes Direct3D 11 and Desktop Duplication support of the machine.</summary>
    internal static class DxProbe
    {
        public static string Run()
        {
            var builder = new StringBuilder();
            D3D11Device device = null;

            try
            {
                device = new D3D11Device(DriverType.Hardware, SharpDX.Direct3D11.DeviceCreationFlags.BgraSupport);
                builder.AppendLine("D3D11: устройство создано, feature level " + device.FeatureLevel);
            }
            catch (Exception ex)
            {
                builder.AppendLine("D3D11 недоступен: " + ex.Message);
            }

            try
            {
                using (var factory = new Factory1())
                {
                    for (var a = 0; ; a++)
                    {
                        Adapter1 adapter;
                        try
                        {
                            adapter = factory.GetAdapter1(a);
                        }
                        catch (SharpDX.SharpDXException)
                        {
                            break;
                        }

                        using (adapter)
                        {
                            builder.AppendLine("Адаптер " + a + ": " + adapter.Description1.Description.Trim());

                            for (var o = 0; ; o++)
                            {
                                Output output;
                                try
                                {
                                    output = adapter.GetOutput(o);
                                }
                                catch (SharpDX.SharpDXException)
                                {
                                    break;
                                }

                                using (output)
                                {
                                    var description = output.Description;
                                    var rect = description.DesktopBounds;
                                    builder.AppendLine(string.Format("  Выход {0}: {1}  {2}×{3} at {4},{5}  attached={6}",
                                        o, description.DeviceName, rect.Right - rect.Left, rect.Bottom - rect.Top,
                                        rect.Left, rect.Top, description.IsAttachedToDesktop));

                                    if (device == null) continue;
                                    try
                                    {
                                        using (var output1 = output.QueryInterface<Output1>())
                                        using (var duplication = output1.DuplicateOutput(device))
                                        {
                                            OutputDuplicateFrameInformation frameInfo;
                                            Resource resource;
                                            var acquired = duplication.TryAcquireNextFrame(500, out frameInfo,
                                                out resource);
                                            builder.AppendLine("    Desktop Duplication: " +
                                                               (acquired.Success
                                                                   ? "доступен"
                                                                   : acquired.Code.ToString()));
                                            if (resource != null) resource.Dispose();
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        builder.AppendLine("    Desktop Duplication: ошибка " + ex.Message);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                builder.AppendLine("Ошибка DXGI: " + ex.Message);
            }
            finally
            {
                if (device != null) device.Dispose();
            }

            return builder.ToString();
        }
    }
}
