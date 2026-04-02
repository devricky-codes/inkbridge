using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Inkbridge.Windows.Services;

namespace Inkbridge.Windows;

public partial class OverlayWindow : Window
{
    private readonly NetworkService _networkService;
    private readonly DispatcherTimer _timer;

    public OverlayWindow(NetworkService networkService)
    {
        InitializeComponent();
        _networkService = networkService;
        
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) }; // 10fps
        _timer.Tick += (s, e) => CaptureAndSend();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _networkService.OnWhiteboardMessage += HandleOverlayMessage;
        _timer.Start();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        ExportPng();
        base.OnClosing(e);
    }

    private void OnClosed(object sender, EventArgs e)
    {
        _timer.Stop();
        _networkService.OnWhiteboardMessage -= HandleOverlayMessage;
        _networkService.BroadcastJsonAsync(JsonSerializer.Serialize(new { type = "wb-overlay-closed" }));
    }

    private void ExportPng()
    {
        try 
        {
            if (OverlayCanvas.ActualWidth <= 0 || OverlayCanvas.ActualHeight <= 0) return;
            
            // Accurately resolve actual screen coordinates
            var screenPoint = this.PointToScreen(new System.Windows.Point(0, 0));
            var bounds = new System.Drawing.Rectangle((int)screenPoint.X, (int)screenPoint.Y, (int)ActualWidth, (int)ActualHeight);

            using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using var gfx = System.Drawing.Graphics.FromImage(bmp);
            // Include layered windows (like this one) to ensure strokes are captured along with the screen
            gfx.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);

            var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var path = System.IO.Path.Combine(dir, $"Inkbridge_Overlay_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png); 
            
            // Set to clipboard using WinForms for direct Bitmap support
            System.Windows.Forms.Clipboard.SetImage(bmp);

            System.Windows.MessageBox.Show($"Overlay captured to clipboard and saved to:\n{path}", "Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
        } 
        catch (Exception ex)
        { 
            System.Windows.MessageBox.Show($"Failed to export overlay: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private void OnWindowDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    public void RequestImmediateFrame()
    {
        Dispatcher.BeginInvoke(CaptureAndSend, DispatcherPriority.Normal);
    }

    private async void CaptureAndSend()
    {
        try
        {
            var bounds = new System.Drawing.Rectangle((int)Left, (int)Top, (int)Width, (int)Height);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using var gfx = System.Drawing.Graphics.FromImage(bmp);
            gfx.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);

            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); // Preserve quality
            var b64 = Convert.ToBase64String(ms.ToArray());

            var msg = JsonSerializer.Serialize(new { type = "wb-overlay-frame", data = b64, w = bounds.Width, h = bounds.Height });
            await _networkService.BroadcastJsonAsync(msg);
        }
        catch { }
    }

    private void HandleOverlayMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "wb-overlay-stroke")
            {
                var points = new List<System.Windows.Point>();
                foreach (var pt in doc.RootElement.GetProperty("points").EnumerateArray())
                    points.Add(new System.Windows.Point(pt.GetProperty("x").GetDouble(), pt.GetProperty("y").GetDouble()));
                var colorVal = (ulong)doc.RootElement.GetProperty("color").GetInt64();
                var c = System.Windows.Media.Color.FromArgb((byte)(colorVal >> 24), (byte)(colorVal >> 16), (byte)(colorVal >> 8), (byte)colorVal);
                var width = doc.RootElement.GetProperty("width").GetDouble();
                var id = doc.RootElement.GetProperty("id").GetString();

                Dispatcher.Invoke(() => {
                    var polyline = new Polyline
                    {
                        Stroke = new SolidColorBrush(c),
                        StrokeThickness = width,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Tag = id
                    };
                    foreach (var pt in points) polyline.Points.Add(pt);
                    OverlayCanvas.Children.Add(polyline);
                });
            }
            else if (type == "wb-overlay-erase")
            {
                var id = doc.RootElement.GetProperty("id").GetString();
                Dispatcher.Invoke(() => {
                    var toRemove = OverlayCanvas.Children.OfType<FrameworkElement>().Where(e => (e.Tag as string) == id).ToList();
                    foreach (var el in toRemove) OverlayCanvas.Children.Remove(el);
                });
            }
            else if (type == "wb-overlay-clear")
            {
                Dispatcher.Invoke(() => OverlayCanvas.Children.Clear());
            }
        }
        catch { }
    }
}
