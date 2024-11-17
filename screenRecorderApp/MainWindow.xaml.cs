using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Drawing;
using System.Drawing.Imaging;
using FFmpeg.AutoGen;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DrawingImaging = System.Drawing.Imaging;

namespace screenRecorderApp
{
    public partial class MainWindow : Window
    {
        private readonly DispatcherTimer timer;
        private int frameCount = 0;
        private TimeSpan elapsedTime;
        private bool isRecording = false;
        private List<string> videoFrames = new List<string>();
        private string videoFilePath = "";
        private readonly string framesDirectory;
        private readonly FFMpeg ffmpeg;
        private bool isSaving = false;
        private readonly object lockObject = new object();

        // Progress window for showing FFmpeg status
        private Window progressWindow;
        private TextBlock progressText;
        private ProgressBar progressBar;

        public MainWindow()
        {
            InitializeComponent();

            try
            {
                FFmpegBinariesHelper.RegisterFFmpegBinaries();
                ffmpeg = new FFMpeg();

                framesDirectory = Path.Combine(Environment.CurrentDirectory, "CapturedFrames");
                EnsureFramesDirectory();

                timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(33.33)
                };
                timer.Tick += CaptureScreen;

                var updateTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                updateTimer.Tick += UpdateTimerDisplay;
                updateTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize application: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (videoFrames.Count == 0)
            {
                MessageBox.Show("No frames captured to save.");
                return;
            }

            if (isSaving)
            {
                MessageBox.Show("Already saving a video. Please wait.");
                return;
            }

            try
            {
                isSaving = true;
                SaveButton.IsEnabled = false;
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = false;

                ShowProgressWindow();

                var progress = new Progress<string>(value =>
                {
                    progressText.Text = value;
                    progressBar.IsIndeterminate = true;
                });

                await SaveRecordingToVideoAsync(progress);

                RecordedVideosListBox.Items.Add(Path.GetFileName(videoFilePath));
                elapsedTime = TimeSpan.Zero;
                TimerTextBlock.Text = "00:00";

                MessageBox.Show("Video saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save the recording: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                isSaving = false;
                SaveButton.IsEnabled = true;
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = true;
                CloseProgressWindow();
            }
        }

        private void ShowProgressWindow()
        {
            progressWindow = new Window
            {
                Title = "Saving Video",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            progressText = new TextBlock
            {
                Text = "Preparing to save video...",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(10)
            };

            progressBar = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 20,
                Margin = new Thickness(10)
            };

            Grid.SetRow(progressText, 0);
            Grid.SetRow(progressBar, 1);

            grid.Children.Add(progressText);
            grid.Children.Add(progressBar);

            progressWindow.Content = grid;
            progressWindow.Show();
        }

        private void CloseProgressWindow()
        {
            if (progressWindow != null)
            {
                progressWindow.Close();
                progressWindow = null;
            }
        }

        private async Task SaveRecordingToVideoAsync(IProgress<string> progress)
        {
            try
            {
                await ffmpeg.CreateVideoFromFramesAsync(videoFrames, videoFilePath, progress);
                await Task.Run(() => CleanupFramesDirectory());
                videoFrames.Clear();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save video: {ex.Message}", ex);
            }
        }

        
        private void EnsureFramesDirectory()
        {
            try
            {
                if (!Directory.Exists(framesDirectory))
                {
                    Directory.CreateDirectory(framesDirectory);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to create frames directory: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateTimerDisplay(object? sender, EventArgs e)
        {
            if (isRecording)
            {
                elapsedTime = elapsedTime.Add(TimeSpan.FromSeconds(1));
                TimerTextBlock.Text = elapsedTime.ToString(@"mm\:ss");  
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (isRecording) return;

            try
            {
                CleanupFramesDirectory();

                elapsedTime = TimeSpan.Zero;
                frameCount = 0;
                videoFrames.Clear();
                videoFilePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    $"screen_recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4"
                );

                isRecording = true;
                await Task.Delay(100); // Small delay to ensure UI is ready
                timer.Start();

                // Update UI to show recording state
                StartButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                SaveButton.IsEnabled = false;

                StatusTextBlock.Text = "Recording...";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start recording: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                isRecording = false;
            }
        }


        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isRecording) return;

            timer.Stop();
            isRecording = false;

            // Update UI
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SaveButton.IsEnabled = true;
            StatusTextBlock.Text = "Recording stopped";
        }

        private void CaptureScreen(object sender, EventArgs e)
        {
            if (!isRecording) return;

            try
            {
                lock (lockObject)  // Add thread safety
                {
                    string frameFileName = $"Frame_{frameCount++:D6}.png";
                    string fullPath = Path.Combine(framesDirectory, frameFileName);

                    using (var screenBmp = new Bitmap(
                        (int)SystemParameters.PrimaryScreenWidth,
                        (int)SystemParameters.PrimaryScreenHeight,
                        DrawingImaging.PixelFormat.Format32bppArgb))  
                    {
                        using (var g = Graphics.FromImage(screenBmp))
                        {
                            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Low;
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                            g.CopyFromScreen(0, 0, 0, 0, screenBmp.Size);
                        }

                        var encoder = GetEncoder(ImageFormat.Png);
                        var encoderParams = new EncoderParameters(1);
                        encoderParams.Param[0] = new EncoderParameter(DrawingImaging.Encoder.Quality, 85L);
                        screenBmp.Save(fullPath, encoder, encoderParams);
                    }

                    videoFrames.Add(fullPath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Frame capture error: {ex.Message}");
         
            }
        }

        // method for image encoding
        private ImageCodecInfo GetEncoder(ImageFormat format)
        {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            return codecs.FirstOrDefault(codec => codec.FormatID == format.Guid)
                ?? throw new InvalidOperationException("PNG encoder not found");
        }


        // New method to clean up frames directory
        private void CleanupFramesDirectory()
        {
            try
            {
                if (Directory.Exists(framesDirectory))
                {
                    foreach (var file in Directory.GetFiles(framesDirectory, "Frame_*.png"))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to delete frame {file}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to clean up frames directory: {ex.Message}");
            }
        }

        //method to clean up when the application closes
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            CleanupFramesDirectory();
        }

        public static class FFmpegBinariesHelper
        {
            private static string GetBinariesPath()
            {
                string ffmpegPath = @"C:\Users\Lenovo\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg.Essentials_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-7.1-essentials_build\bin";

                if (!Directory.Exists(ffmpegPath))
                {
                    throw new DirectoryNotFoundException($"FFmpeg binaries not found in: {ffmpegPath}");
                }

                return ffmpegPath;
            }

            public static void RegisterFFmpegBinaries()
            {
                try
                {
                    string ffmpegPath = GetBinariesPath();
                    SetDllDirectory(ffmpegPath);

                    string ffmpegExe = Path.Combine(ffmpegPath, "ffmpeg.exe");
                    if (!File.Exists(ffmpegExe))
                    {
                        throw new FileNotFoundException($"FFmpeg executable not found at: {ffmpegExe}");
                    }

                    Debug.WriteLine($"FFmpeg binaries registered successfully from: {ffmpegPath}");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to register FFmpeg binaries: {ex.Message}", ex);
                }
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool SetDllDirectory(string lpPathName);
        }

        public class FFMpeg
        {
            private readonly string ffmpegPath;

            public FFMpeg()
            {
                ffmpegPath = Path.Combine(
                    @"C:\Users\Lenovo\AppData\Local\Microsoft\WinGet\Packages",
                    @"Gyan.FFmpeg.Essentials_Microsoft.Winget.Source_8wekyb3d8bbwe",
                    @"ffmpeg-7.1-essentials_build\bin",
                    "ffmpeg.exe"
                );

                if (!File.Exists(ffmpegPath))
                {
                    throw new FileNotFoundException($"FFmpeg executable not found at: {ffmpegPath}");
                }
            }

            public async Task CreateVideoFromFramesAsync(List<string> frames, string outputFile, IProgress<string> progress)
            {
                if (frames == null || frames.Count == 0)
                    throw new ArgumentException("No frames provided.", nameof(frames));

                if (string.IsNullOrWhiteSpace(outputFile))
                    throw new ArgumentException("Output file path cannot be empty.", nameof(outputFile));

                string tempFileList = Path.Combine(Path.GetTempPath(), $"ffmpeg_input_{Guid.NewGuid()}.txt");

                try
                {
                    progress.Report("Creating input file list...");
                    await File.WriteAllLinesAsync(tempFileList,
                        frames.Select(frame => $"file '{frame.Replace("\\", "/")}'"));

                    // Modified FFmpeg command for better quality
                    string command = $"-f concat -safe 0 -i \"{tempFileList}\" " +
                                   $"-c:v libx264 -preset medium -crf 23 " +
                                   $"-r 30 -pix_fmt yuv420p \"{outputFile}\"";

                    progress.Report("Starting FFmpeg process...");

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = command,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var process = new Process { StartInfo = startInfo };

                    StringBuilder outputBuilder = new StringBuilder();
                    StringBuilder errorBuilder = new StringBuilder();

                    process.OutputDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputBuilder.AppendLine(e.Data);
                            progress.Report($"Processing: {e.Data}");
                        }
                    };

                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errorBuilder.AppendLine(e.Data);
                            progress.Report($"FFmpeg: {e.Data}");
                        }
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    await Task.Run(() => process.WaitForExit());

                    if (process.ExitCode != 0)
                    {
                        throw new Exception($"FFmpeg failed with exit code {process.ExitCode}. Error: {errorBuilder}");
                    }

                    progress.Report("Video creation completed successfully.");
                }
                finally
                {
                    if (File.Exists(tempFileList))
                    {
                        try
                        {
                            File.Delete(tempFileList);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to delete temporary file: {ex.Message}");
                        }
                    }
                }
            }
        }
    }

}