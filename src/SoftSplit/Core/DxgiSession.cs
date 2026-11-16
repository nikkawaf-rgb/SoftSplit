using System;
using System.Collections.Generic;
using System.Drawing;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using Buffer = SharpDX.Direct3D11.Buffer;
using Device = SharpDX.Direct3D11.Device;
using Rectangle = System.Drawing.Rectangle;
using Resource = SharpDX.DXGI.Resource;
using Size = System.Drawing.Size;

namespace SoftSplit.Core
{
    /// <summary>
    /// GPU path: captures a display with Desktop Duplication and presents it on the
    /// target window with a swap chain. The frame never leaves the graphics card, so
    /// the CPU only wakes up when the source picture actually changes.
    /// </summary>
    internal sealed class DxgiSession : IDisposable
    {
        private const string ShaderSource = @"
cbuffer UVTransform : register(b0) { float4 UV; };

Texture2D FrameTexture : register(t0);
SamplerState FrameSampler : register(s0);

struct VSOut { float4 Pos : SV_POSITION; float2 Tex : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    float2 t = float2((id << 1) & 2, id & 2);
    VSOut o;
    o.Pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Tex = t * UV.xy + UV.zw;
    return o;
}

float4 PSMain(VSOut input) : SV_TARGET
{
    return FrameTexture.Sample(FrameSampler, input.Tex);
}
";

        private sealed class Source
        {
            public string GdiName;
            public Rectangle Bounds;
            public OutputDuplication Duplication;
            public Texture2D Frame;
            public ShaderResourceView View;
        }

        private sealed class Target
        {
            public string Key;
            public string SourceGdi;
            public IntPtr Hwnd;
            public Rectangle Bounds;
            public ScaleMode Scale;
            public SwapChain1 SwapChain;
            public RenderTargetView RenderTarget;
            public bool NeedsPresent = true;
        }

        private readonly Dictionary<string, Source> _sources =
            new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Target> _targets =
            new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);

        private Factory1 _factory;
        private Factory2 _factory2;
        private Device _device;
        private DeviceContext _context;
        private VertexShader _vertexShader;
        private PixelShader _pixelShader;
        private SamplerState _sampler;
        private Buffer _constantBuffer;
        private BlendState _alphaBlend;
        private Texture2D _cursorTexture;
        private ShaderResourceView _cursorView;
        private IntPtr _cursorHandle;
        private int _cursorHotspotX;
        private int _cursorHotspotY;
        private bool _cursorReady;
        private Native.POINT _cursorPosition;
        private bool _disposed;

        /// <summary>Draw the mouse cursor over the mirrored picture.</summary>
        public bool ShowCursor { get; set; }

        public Action<string> Log { get; set; }

        /// <summary>Creates the session or returns null when the GPU path is not available.</summary>
        public static DxgiSession TryCreate(Action<string> log)
        {
            var session = new DxgiSession { Log = log };
            try
            {
                session.Initialize();
                return session;
            }
            catch (Exception ex)
            {
                log("GPU path unavailable: " + ex.Message);
                session.Dispose();
                return null;
            }
        }

        private void Initialize()
        {
            _factory = new Factory1();

            Adapter1 adapter = null;
            for (var i = 0; ; i++)
            {
                Adapter1 candidate;
                try
                {
                    candidate = _factory.GetAdapter1(i);
                }
                catch (SharpDXException)
                {
                    break;
                }

                // Microsoft Basic Render Driver is software; prefer a real adapter.
                var isSoftware = (candidate.Description1.Flags & AdapterFlags.Software) != 0;
                if (adapter == null || (!isSoftware && true))
                {
                    if (adapter != null) adapter.Dispose();
                    adapter = candidate;
                    if (!isSoftware) break;
                }
                else
                {
                    candidate.Dispose();
                }
            }

            if (adapter == null) throw new InvalidOperationException("Нет доступных видеоадаптеров.");

            _device = new Device(adapter, DeviceCreationFlags.BgraSupport);
            _context = _device.ImmediateContext;
            _factory2 = _factory.QueryInterface<Factory2>();

            using (var vertexBytecode = ShaderBytecode.Compile(ShaderSource, "VSMain", "vs_5_0"))
                _vertexShader = new VertexShader(_device, vertexBytecode);

            using (var pixelBytecode = ShaderBytecode.Compile(ShaderSource, "PSMain", "ps_5_0"))
                _pixelShader = new PixelShader(_device, pixelBytecode);

            _sampler = new SamplerState(_device, new SamplerStateDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunction = Comparison.Never,
                MaximumLod = float.MaxValue
            });

            _constantBuffer = new Buffer(_device, Utilities.SizeOf<Vector4>(), ResourceUsage.Default,
                BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

            var blend = new BlendStateDescription();
            blend.RenderTarget[0].IsBlendEnabled = true;
            blend.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
            blend.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
            blend.RenderTarget[0].BlendOperation = BlendOperation.Add;
            blend.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
            blend.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
            blend.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
            blend.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
            _alphaBlend = new BlendState(_device, blend);

            _cursorTexture = new Texture2D(_device, new Texture2DDescription
            {
                Width = CursorImage.Size,
                Height = CursorImage.Size,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.Write,
                OptionFlags = ResourceOptionFlags.None
            });
            _cursorView = new ShaderResourceView(_device, _cursorTexture);

            adapter.Dispose();
        }

        public bool AddSource(string gdiName, Rectangle bounds)
        {
            if (_sources.ContainsKey(gdiName)) return true;

            try
            {
                var output = FindOutput(gdiName);
                if (output == null)
                {
                    Log("output not found for " + gdiName);
                    return false;
                }

                using (output)
                using (var output1 = output.QueryInterface<Output1>())
                {
                    var duplication = output1.DuplicateOutput(_device);
                    _sources[gdiName] = new Source
                    {
                        GdiName = gdiName,
                        Bounds = bounds,
                        Duplication = duplication
                    };
                }

                return true;
            }
            catch (Exception ex)
            {
                Log("duplicate output failed for " + gdiName + ": " + ex.Message);
                return false;
            }
        }

        public bool AddTarget(string key, string sourceGdi, IntPtr hwnd, Rectangle bounds, ScaleMode scale)
        {
            if (_targets.ContainsKey(key)) return true;

            try
            {
                var description = new SwapChainDescription1
                {
                    Width = Math.Max(1, bounds.Width),
                    Height = Math.Max(1, bounds.Height),
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipSequential,
                    AlphaMode = AlphaMode.Ignore,
                    Flags = SwapChainFlags.None
                };

                var swapChain = new SwapChain1(_factory2, _device, hwnd, ref description);
                var backBuffer = swapChain.GetBackBuffer<Texture2D>(0);
                var renderTarget = new RenderTargetView(_device, backBuffer);
                backBuffer.Dispose();

                _targets[key] = new Target
                {
                    Key = key,
                    SourceGdi = sourceGdi,
                    Hwnd = hwnd,
                    Bounds = bounds,
                    Scale = scale,
                    SwapChain = swapChain,
                    RenderTarget = renderTarget
                };

                return true;
            }
            catch (Exception ex)
            {
                Log("swap chain failed for " + key + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Captures changed sources and presents them. Returns true when anything was drawn.</summary>
        public bool Tick()
        {
            if (_disposed) return false;

            var cursorChanged = RefreshCursor();
            if (cursorChanged)
            {
                // Desktop Duplication may not deliver a frame when only the pointer
                // moved, so ask every target to repaint itself with the new cursor.
                foreach (var target in _targets.Values) target.NeedsPresent = true;
            }

            var changed = false;
            foreach (var source in _sources.Values)
            {
                if (Acquire(source)) changed = true;
            }

            if (!changed)
            {
                foreach (var target in _targets.Values)
                {
                    if (!target.NeedsPresent) continue;
                    Present(target);
                }
                return false;
            }

            foreach (var target in _targets.Values)
            {
                Present(target);
            }

            return true;
        }

        private bool Acquire(Source source)
        {
            OutputDuplicateFrameInformation frameInfo;
            Resource resource;
            var result = source.Duplication.TryAcquireNextFrame(0, out frameInfo, out resource);
            if (!result.Success)
            {
                if (result.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
                    throw new SharpDXException(result);
                return false;
            }

            try
            {
                using (resource)
                using (var texture = resource.QueryInterface<Texture2D>())
                {
                    if (source.Frame == null ||
                        source.Frame.Description.Width != texture.Description.Width ||
                        source.Frame.Description.Height != texture.Description.Height)
                    {
                        if (source.View != null) source.View.Dispose();
                        if (source.Frame != null) source.Frame.Dispose();

                        source.Frame = new Texture2D(_device, new Texture2DDescription
                        {
                            Width = texture.Description.Width,
                            Height = texture.Description.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = texture.Description.Format,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Default,
                            BindFlags = BindFlags.ShaderResource,
                            CpuAccessFlags = CpuAccessFlags.None,
                            OptionFlags = ResourceOptionFlags.None
                        });

                        source.View = new ShaderResourceView(_device, source.Frame);
                    }

                    _context.CopyResource(texture, source.Frame);
                }
            }
            finally
            {
                source.Duplication.ReleaseFrame();
            }

            return true;
        }

        private void Present(Target target)
        {
            Source source;
            if (!_sources.TryGetValue(target.SourceGdi, out source) || source.View == null) return;

            try
            {
                var client = new Rectangle(0, 0, target.Bounds.Width, target.Bounds.Height);
                Rectangle destination;
                Rectangle crop;
                Placement.Compute(new Size(source.Frame.Description.Width, source.Frame.Description.Height),
                    client, target.Scale, out destination, out crop);

                var uv = new Vector4(
                    crop.Width / (float)source.Frame.Description.Width,
                    crop.Height / (float)source.Frame.Description.Height,
                    crop.X / (float)source.Frame.Description.Width,
                    crop.Y / (float)source.Frame.Description.Height);

                _context.UpdateSubresource(ref uv, _constantBuffer);

                _context.OutputMerger.SetRenderTargets(target.RenderTarget);
                _context.ClearRenderTargetView(target.RenderTarget, new RawColor4(0, 0, 0, 1));

                _context.Rasterizer.SetViewport(new Viewport(destination.X, destination.Y,
                    destination.Width, destination.Height, 0.0f, 1.0f));
                _context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                _context.VertexShader.Set(_vertexShader);
                _context.VertexShader.SetConstantBuffer(0, _constantBuffer);
                _context.PixelShader.Set(_pixelShader);
                _context.PixelShader.SetSampler(0, _sampler);
                _context.PixelShader.SetShaderResource(0, source.View);
                _context.Draw(3, 0);
                _context.PixelShader.SetShaderResource(0, null);

                if (ShowCursor) DrawCursor(target, source, destination);

                target.SwapChain.Present(0, PresentFlags.None);
                target.NeedsPresent = false;
            }
            catch (SharpDXException ex)
            {
                if (ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved ||
                    ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset)
                    throw;
                target.NeedsPresent = true;
                Log("present failed: " + ex.Message);
            }
        }

        private int _logCount;

        private void LogOnce(string message)
        {
            if (_logCount > 20) return;
            _logCount++;
            var log = Log;
            if (log != null) log(message);
        }

        /// <summary>Marks every target as needing a repaint (after a layout change).</summary>
        public void Invalidate()
        {
            foreach (var target in _targets.Values) target.NeedsPresent = true;
        }

        private bool RefreshCursor()
        {
            if (!ShowCursor)
            {
                _cursorReady = false;
                return false;
            }

            var info = new Native.CURSORINFO();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.CURSORINFO));
            var ok = Native.GetCursorInfo(ref info);

            if (!ok ||
                (info.flags & Native.CURSOR_SHOWING) == 0 ||
                info.hCursor == IntPtr.Zero)
            {
                var wasReady = _cursorReady;
                _cursorReady = false;
                return wasReady;
            }

            var moved = info.ptScreenPos.X != _cursorPosition.X || info.ptScreenPos.Y != _cursorPosition.Y;
            _cursorPosition = info.ptScreenPos;

            if (info.hCursor == _cursorHandle && _cursorReady) return moved;

            int hotspotX;
            int hotspotY;
            byte[] pixels;
            try
            {
                pixels = CursorImage.Capture(out hotspotX, out hotspotY);
            }
            catch (Exception ex)
            {
                LogOnce("cursor capture failed: " + ex.Message);
                _cursorReady = false;
                return false;
            }

            if (pixels == null)
            {
                LogOnce("cursor capture returned null");
                _cursorReady = false;
                return false;
            }

            try
            {
                UploadCursor(pixels);
            }
            catch (Exception)
            {
                _cursorReady = false;
                return false;
            }

            _cursorHandle = info.hCursor;
            _cursorHotspotX = hotspotX;
            _cursorHotspotY = hotspotY;
            _cursorReady = true;
            return true;
        }

        private void UploadCursor(byte[] pixels)
        {
            var stride = CursorImage.Size * 4;
            var box = _context.MapSubresource(_cursorTexture, 0, MapMode.WriteDiscard,
                SharpDX.Direct3D11.MapFlags.None);
            for (var y = 0; y < CursorImage.Size; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(pixels, y * stride,
                    box.DataPointer + y * box.RowPitch, stride);
            }
            _context.UnmapSubresource(_cursorTexture, 0);
        }

        private void DrawCursor(Target target, Source source, Rectangle destination)
        {
            if (!_cursorReady) return;

            var sourceWidth = source.Frame.Description.Width;
            var sourceHeight = source.Frame.Description.Height;
            var relativeX = _cursorPosition.X - source.Bounds.X;
            var relativeY = _cursorPosition.Y - source.Bounds.Y;

            // The pointer belongs to another display: nothing to draw here.
            if (relativeX < -CursorImage.Size || relativeY < -CursorImage.Size ||
                relativeX > sourceWidth + CursorImage.Size || relativeY > sourceHeight + CursorImage.Size)
                return;

            var scale = destination.Width / (float)sourceWidth;
            var size = (int)Math.Round(CursorImage.Size * scale);
            if (size <= 0) return;

            var x = destination.X + (int)Math.Round((relativeX - _cursorHotspotX) * scale);
            var y = destination.Y + (int)Math.Round((relativeY - _cursorHotspotY) * scale);

            var uv = new Vector4(1f, 1f, 0f, 0f);
            _context.UpdateSubresource(ref uv, _constantBuffer);

            _context.Rasterizer.SetViewport(new Viewport(x, y, size, size, 0.0f, 1.0f));
            _context.OutputMerger.SetBlendState(_alphaBlend);
            _context.PixelShader.SetShaderResource(0, _cursorView);
            _context.Draw(3, 0);
            _context.OutputMerger.SetBlendState(null);
            _context.PixelShader.SetShaderResource(0, null);
        }

        private Output FindOutput(string gdiName)
        {
            for (var a = 0; ; a++)
            {
                Adapter1 adapter;
                try
                {
                    adapter = _factory.GetAdapter1(a);
                }
                catch (SharpDXException)
                {
                    return null;
                }

                using (adapter)
                {
                    for (var o = 0; ; o++)
                    {
                        Output output;
                        try
                        {
                            output = adapter.GetOutput(o);
                        }
                        catch (SharpDXException)
                        {
                            break;
                        }

                        if (string.Equals(output.Description.DeviceName, gdiName,
                                StringComparison.OrdinalIgnoreCase))
                            return output;

                        output.Dispose();
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var target in _targets.Values)
            {
                if (target.RenderTarget != null) target.RenderTarget.Dispose();
                if (target.SwapChain != null) target.SwapChain.Dispose();
            }
            _targets.Clear();

            foreach (var source in _sources.Values)
            {
                if (source.View != null) source.View.Dispose();
                if (source.Frame != null) source.Frame.Dispose();
                if (source.Duplication != null) source.Duplication.Dispose();
            }
            _sources.Clear();

            if (_constantBuffer != null) _constantBuffer.Dispose();
            if (_cursorView != null) _cursorView.Dispose();
            if (_cursorTexture != null) _cursorTexture.Dispose();
            if (_alphaBlend != null) _alphaBlend.Dispose();
            if (_sampler != null) _sampler.Dispose();
            if (_pixelShader != null) _pixelShader.Dispose();
            if (_vertexShader != null) _vertexShader.Dispose();
            if (_context != null) _context.Dispose();
            if (_device != null) _device.Dispose();
            if (_factory2 != null) _factory2.Dispose();
            if (_factory != null) _factory.Dispose();
        }
    }
}
