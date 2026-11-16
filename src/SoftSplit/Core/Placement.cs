using System;
using System.Drawing;

namespace SoftSplit.Core
{
    /// <summary>Computes how a captured frame maps onto the target display.</summary>
    internal static class Placement
    {
        public static void Compute(Size frame, Rectangle client, ScaleMode mode,
            out Rectangle destination, out Rectangle source)
        {
            var frameRatio = frame.Width / (float)frame.Height;
            var clientRatio = client.Width / (float)client.Height;

            switch (mode)
            {
                case ScaleMode.Stretch:
                    source = new Rectangle(0, 0, frame.Width, frame.Height);
                    destination = client;
                    return;

                case ScaleMode.Fill:
                {
                    source = new Rectangle(0, 0, frame.Width, frame.Height);
                    if (frameRatio > clientRatio)
                    {
                        var croppedWidth = (int)Math.Round(frame.Height * clientRatio);
                        source = new Rectangle((frame.Width - croppedWidth) / 2, 0, croppedWidth, frame.Height);
                    }
                    else
                    {
                        var croppedHeight = (int)Math.Round(frame.Width / clientRatio);
                        source = new Rectangle(0, (frame.Height - croppedHeight) / 2, frame.Width, croppedHeight);
                    }
                    destination = client;
                    return;
                }

                default:
                {
                    source = new Rectangle(0, 0, frame.Width, frame.Height);
                    int width;
                    int height;
                    if (frameRatio > clientRatio)
                    {
                        width = client.Width;
                        height = (int)Math.Round(client.Width / frameRatio);
                    }
                    else
                    {
                        height = client.Height;
                        width = (int)Math.Round(client.Height * frameRatio);
                    }
                    destination = new Rectangle(client.X + (client.Width - width) / 2,
                        client.Y + (client.Height - height) / 2, width, height);
                    return;
                }
            }
        }
    }
}
