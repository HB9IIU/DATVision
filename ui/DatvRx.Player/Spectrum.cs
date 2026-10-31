// Adapted from HB9IIU Experimental H.266 Player, Spectrum.cs (GPL-3.0).
// Original project: https://github.com/HB9IIU/HB9IIU-Experimental-H266-Player
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DatvRx.Player
{
    // The BATC wideband spectrum of the QO-100 transponder: the same public feed and the same
    // signal finder as the QOCAST page (static/js/batc-spectrum.js), without QOCAST.
    public static class Band
    {
        public const double StartMhz = 10490.5;
        public const double EndMhz = 10499.5;
        public const double BeaconMhz = 10491.5;
        public static readonly int[] StandardSrs = { 25, 33, 50, 66, 125, 250, 333, 500, 1000, 1500, 2000 };
        public static readonly double[] SlotCenters = Enumerable.Range(0, 14).Select(i => 10492.75 + i * 0.5).ToArray();
    }

    public struct FoundSignal
    {
        public double CenterMhz;
        public int SymbolRate;
    }

    // Finds the signal under a click: centre and symbol rate from its width at half power.
    public static class SignalFinder
    {
        public static FoundSignal? Find(ushort[] frame, double clickedMhz)
        {
            int bins = frame.Length;
            if (bins < 16) return null;
            double mhzPerBin = (Band.EndMhz - Band.StartMhz) / (bins - 1);
            double[] values = Smooth(frame);
            double[] sorted = (double[])values.Clone();
            Array.Sort(sorted);
            double noise = sorted[sorted.Length / 2];
            if (!double.IsFinite(clickedMhz) || clickedMhz < Band.StartMhz || clickedMhz > Band.EndMhz) return null;
            int clickedBin = Math.Clamp((int)Math.Round((clickedMhz - Band.StartMhz) / mhzPerBin), 0, bins - 1);
            int radius = Math.Max(1, (int)Math.Round(0.1 / mhzPerBin));
            int peak = Math.Max(0, clickedBin - radius);
            for (int i = peak; i <= Math.Min(bins - 1, clickedBin + radius); i++)
                if (values[i] > values[peak]) peak = i;
            if (values[peak] - noise < 2000) return null;
            double halfPower = noise + (values[peak] - noise) / 2;
            int left = peak, right = peak;
            while (left > 0 && values[left - 1] > halfPower) left--;
            while (right < bins - 1 && values[right + 1] > halfPower) right++;
            double center = Band.StartMhz + ((left + right) / 2.0) * mhzPerBin;
            int sr = NearestSr((right - left + 1) * mhzPerBin * 1000);
            if (Math.Abs(center - Band.BeaconMhz) < 0.3) { center = Band.BeaconMhz; sr = 1500; }
            return new FoundSignal { CenterMhz = center, SymbolRate = sr };
        }

        private static double[] Smooth(ushort[] frame)
        {
            var output = new double[frame.Length];
            for (int i = 0; i < frame.Length; i++)
            {
                double sum = 0;
                int count = 0;
                for (int s = Math.Max(0, i - 3); s <= Math.Min(frame.Length - 1, i + 3); s++) { sum += frame[s]; count++; }
                output[i] = sum / count;
            }
            return output;
        }

        private static int NearestSr(double measured)
        {
            return Band.StandardSrs.OrderBy(v => Math.Abs(Math.Log(v / measured))).First();
        }
    }

    // wss://eshail.batc.org.uk/wb/fft : binary messages of 16-bit samples, one per picture of the spectrum.
    public sealed class SpectrumFeed : IDisposable
    {
        private const string Url = "wss://eshail.batc.org.uk/wb/fft";
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();

        public event Action<ushort[]> FrameReceived;
        public event Action<bool> ConnectionChanged;

        public void Start()
        {
            Task.Run(() => Loop(_stop.Token));
        }

        private async Task Loop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var socket = new ClientWebSocket())
                    {
                        socket.Options.AddSubProtocol("fft");
                        await socket.ConnectAsync(new Uri(Url), token);
                        ConnectionChanged?.Invoke(true);
                        var buffer = new byte[64 * 1024];
                        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                        {
                            int length = 0;
                            WebSocketReceiveResult result;
                            do
                            {
                                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), token);
                                length += result.Count;
                            } while (!result.EndOfMessage && length < buffer.Length);
                            if (result.MessageType == WebSocketMessageType.Close || !result.EndOfMessage) break;
                            if (result.MessageType != WebSocketMessageType.Binary || length < 32 || length % 2 != 0) continue;
                            var frame = new ushort[length / 2];
                            Buffer.BlockCopy(buffer, 0, frame, 0, frame.Length * 2);
                            FrameReceived?.Invoke(frame);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Spectrum feed: " + ex.Message);
                }
                ConnectionChanged?.Invoke(false);
                try { await Task.Delay(2000, token); } catch (OperationCanceledException) { }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
        }
    }

    // The little spectrum at the bottom of the window. A click on a signal asks to tune it.
    public sealed class SpectrumView : System.Windows.Forms.Control
    {
        private const double Floor = 0.12, Ceiling = 0.58;
        private ushort[] _frame;
        private bool _connected;
        private double? _tunedMhz;
        private int _tunedSr;
        private string _message = "";
        private DateTime _messageUntil = DateTime.MinValue;

        // MHz under the click; the window finds the signal and tunes
        public event Action<double> SpectrumClicked;

        public SpectrumView()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(5, 20, 29);
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        public ushort[] Frame { get { return _frame; } }
        public double? TunedMhz { get { return _tunedMhz; } }
        public int TunedSr { get { return _tunedSr; } }

        public void SetFrame(ushort[] frame) { _frame = frame; Invalidate(); }
        public void SetConnected(bool connected) { _connected = connected; Invalidate(); }
        public void SetTuned(double? centerMhz, int symbolRate) { _tunedMhz = centerMhz; _tunedSr = symbolRate; Invalidate(); }

        public void ShowMessage(string text)
        {
            _message = text;
            _messageUntil = DateTime.UtcNow.AddSeconds(4);
            Invalidate();
            var timer = new System.Windows.Forms.Timer { Interval = 4100 };
            timer.Tick += (s, e) => { timer.Stop(); timer.Dispose(); Invalidate(); };
            timer.Start();
        }

        private float X(double mhz) { return (float)((mhz - Band.StartMhz) * Width / (Band.EndMhz - Band.StartMhz)); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && Width > 0)
                SpectrumClicked?.Invoke(Band.StartMhz + e.X * (Band.EndMhz - Band.StartMhz) / Width);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            int bottom = Height - 2;                           // no channel marks or scale: the trace uses the full height
            int top = 2 + (int)((bottom - 2) * 0.10);        // headroom: the beacon stays 10% below the top
            float plot = Math.Max(1, bottom - top);

            // tuned band: symbol rate x 1.35, under the trace
            if (_tunedMhz.HasValue)
            {
                double half = _tunedSr * 1.35 / 1000 / 2;
                float l = X(_tunedMhz.Value - half), r = X(_tunedMhz.Value + half);
                using (var fill = new SolidBrush(Color.FromArgb(46, 37, 194, 110)))
                using (var edge = new Pen(Color.FromArgb(37, 194, 110)))
                {
                    g.FillRectangle(fill, l, top, r - l, plot);
                    g.DrawRectangle(edge, l, top, r - l, plot - 1);
                }
            }

            if (_frame != null && _frame.Length > 1 && Width > 1)
            {
                var points = new List<PointF> { new PointF(0, bottom) };
                for (int x = 0; x < Width; x++)
                {
                    double src = x * (_frame.Length - 1) / (double)Math.Max(1, Width - 1);
                    int left = (int)Math.Floor(src);
                    double sample = _frame[left] + (src - left) * (_frame[Math.Min(left + 1, _frame.Length - 1)] - _frame[left]);
                    double n = Math.Max(0, Math.Min(1, (sample / 65535.0 - Floor) / (Ceiling - Floor)));
                    points.Add(new PointF(x, (float)(bottom - n * plot)));
                }
                points.Add(new PointF(Width - 1, bottom));
                using (var fill = new SolidBrush(Color.FromArgb(55, 255, 176, 0)))
                    g.FillPolygon(fill, points.ToArray());
                using (var line = new Pen(Color.FromArgb(255, 176, 0), 0.6f))
                    g.DrawLines(line, points.Skip(1).Take(points.Count - 2).ToArray());
            }
            else
            {
                using (var brush = new SolidBrush(Color.FromArgb(145, 164, 181)))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(_connected ? "Waiting for BATC spectrum data..." : "No connection to the BATC spectrum (internet needed)",
                                 Font, brush, new RectangleF(0, top, Width, plot), format);
            }

            if (DateTime.UtcNow < _messageUntil && _message.Length > 0)
            {
                SizeF size = g.MeasureString(_message, Font);
                using (var back = new SolidBrush(Color.FromArgb(225, 5, 20, 29)))
                    g.FillRectangle(back, 4, 2, size.Width + 8, size.Height + 2);
                using (var brush = new SolidBrush(Color.FromArgb(214, 227, 236)))
                    g.DrawString(_message, Font, brush, 8, 3);
            }
        }
    }
}

