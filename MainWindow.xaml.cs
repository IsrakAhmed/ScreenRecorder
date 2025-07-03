using Microsoft.Win32;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ScreenRecorder
{
    public partial class MainWindow : Window
    {
        private Process ffmpegProcess;
        private string outputFilePath;
        private string tempDir;
        private List<string> segmentFiles;
        private DispatcherTimer timer;
        private DateTime startTime;
        private TimeSpan pausedTime;
        private bool isPaused;
        private string systemAudioDevice = null;
        private string micAudioDevice = null;
        private int segmentCount;

        private AppSettings settings;
        private readonly string settingsFile = "settings.json";

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += Timer_Tick;
            segmentFiles = new List<string>();
            segmentCount = 0;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            LoadAudioDevices();

            if (!string.IsNullOrEmpty(settings.AudioSourceType))
            {
                foreach (ComboBoxItem item in cmbAudioSourceType.Items)
                {
                    if (item.Tag?.ToString() == settings.AudioSourceType)
                    {
                        cmbAudioSourceType.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void LoadSettings()
        {
            if (File.Exists(settingsFile))
            {
                string json = File.ReadAllText(settingsFile);
                settings = JsonSerializer.Deserialize<AppSettings>(json);
            }
            else
            {
                settings = new AppSettings
                {
                    BasePath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    AudioSourceType = "none"
                };
            }
        }

        private void SaveSettings()
        {
            settings.AudioSourceType = (cmbAudioSourceType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            settings.SelectedDevice = cmbAudioDevices.SelectedItem?.ToString();
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void LoadAudioDevices()
        {
            var enumerator = new MMDeviceEnumerator();
            var wasapiDevices = GetFFmpegWasapiDevices();
            var dshowDevices = GetFFmpegDshowDevices();
            var allDevices = new List<string>();
            allDevices.AddRange(wasapiDevices);
            allDevices.AddRange(dshowDevices.FindAll(d => !wasapiDevices.Contains(d))); // Avoid duplicates
            Debug.WriteLine("FFmpeg WASAPI devices: " + (wasapiDevices.Count > 0 ? string.Join(", ", wasapiDevices) : "None detected"));
            Debug.WriteLine("FFmpeg DirectShow devices: " + (dshowDevices.Count > 0 ? string.Join(", ", dshowDevices) : "None detected"));

            try
            {
                var defaultOut = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                systemAudioDevice = defaultOut.FriendlyName;
                Debug.WriteLine($"System audio device detected (NAudio): {systemAudioDevice}");
                systemAudioDevice = FindMatchingDevice(systemAudioDevice, wasapiDevices, "output") ?? FindMatchingDevice(systemAudioDevice, dshowDevices, "output");

                // Fallback to Stereo Mix for system audio
                if (dshowDevices.Contains("Stereo Mix (Realtek(R) Audio)") && !ValidateAudioDevice(systemAudioDevice))
                {
                    systemAudioDevice = "Stereo Mix (Realtek(R) Audio)";
                    Debug.WriteLine("Falling back to Stereo Mix (Realtek(R) Audio) for system audio.");
                }

                var defaultMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                micAudioDevice = defaultMic.FriendlyName;
                Debug.WriteLine($"Microphone device detected (NAudio): {micAudioDevice}");
                micAudioDevice = FindMatchingDevice(micAudioDevice, wasapiDevices, "input") ?? FindMatchingDevice(micAudioDevice, dshowDevices, "input");

                Debug.WriteLine($"System audio device selected: {systemAudioDevice}");
                Debug.WriteLine($"Microphone device selected: {micAudioDevice}");

                if (string.IsNullOrEmpty(systemAudioDevice) || string.IsNullOrEmpty(micAudioDevice))
                {
                    System.Windows.MessageBox.Show("Could not match audio devices with FFmpeg. Please select devices manually from the list.");
                    cmbAudioDevices.ItemsSource = allDevices;
                }
                else
                {
                    cmbAudioDevices.ItemsSource = new List<string> { systemAudioDevice, micAudioDevice };
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error detecting audio devices: {ex.Message}\nPlease select devices manually.");
                cmbAudioDevices.ItemsSource = allDevices;
            }

            if (!string.IsNullOrEmpty(settings.SelectedDevice) && cmbAudioDevices.Items.Contains(settings.SelectedDevice))
                cmbAudioDevices.SelectedItem = settings.SelectedDevice;
        }

        private List<string> GetFFmpegWasapiDevices()
        {
            var devices = new List<string>();
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-list_devices true -f wasapi -i dummy",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };

                StringBuilder output = new StringBuilder();
                process.ErrorDataReceived += (s, ev) => { if (ev.Data != null) output.AppendLine(ev.Data); };
                process.Start();
                process.BeginErrorReadLine();
                process.WaitForExit(5000);

                string[] lines = output.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.Contains("(audio)") && line.Contains("wasapi"))
                    {
                        int startIndex = line.IndexOf('"') + 1;
                        int endIndex = line.LastIndexOf('"');
                        if (startIndex > 0 && endIndex > startIndex)
                        {
                            string deviceName = line.Substring(startIndex, endIndex - startIndex);
                            devices.Add(deviceName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error listing FFmpeg WASAPI devices: {ex.Message}");
            }
            return devices;
        }

        private List<string> GetFFmpegDshowDevices()
        {
            var devices = new List<string>();
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-list_devices true -f dshow -i dummy",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };

                StringBuilder output = new StringBuilder();
                process.ErrorDataReceived += (s, ev) => { if (ev.Data != null) output.AppendLine(ev.Data); };
                process.Start();
                process.BeginErrorReadLine();
                process.WaitForExit(5000);

                string[] lines = output.ToString().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    if (line.Contains("(audio)") && line.Contains("dshow"))
                    {
                        int startIndex = line.IndexOf('"') + 1;
                        int endIndex = line.LastIndexOf('"');
                        if (startIndex > 0 && endIndex > startIndex)
                        {
                            string deviceName = line.Substring(startIndex, endIndex - startIndex);
                            devices.Add(deviceName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error listing FFmpeg DirectShow devices: {ex.Message}");
            }
            return devices;
        }

        private string FindMatchingDevice(string naudioDevice, List<string> ffmpegDevices, string deviceType)
        {
            if (string.IsNullOrEmpty(naudioDevice) || ffmpegDevices == null || ffmpegDevices.Count == 0)
                return naudioDevice;

            foreach (var device in ffmpegDevices)
            {
                bool isMatch = device.Contains(naudioDevice, StringComparison.OrdinalIgnoreCase) ||
                               naudioDevice.Contains(device, StringComparison.OrdinalIgnoreCase) ||
                               (device.ToLower().Contains("realtek") && naudioDevice.ToLower().Contains("realtek"));
                bool isCorrectType = (deviceType == "output" && (device.ToLower().Contains("speaker") || device.ToLower().Contains("output") || device.ToLower().Contains("stereo mix"))) ||
                                    (deviceType == "input" && (device.ToLower().Contains("mic") || device.ToLower().Contains("input")));
                if (isMatch && isCorrectType)
                {
                    return device;
                }
            }
            return null; // Return null to trigger fallback
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            TimeSpan elapsed = isPaused ? pausedTime : (DateTime.Now - startTime + pausedTime);
            txtElapsed.Text = $"Time Elapsed: {elapsed:hh\\:mm\\:ss}";
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder to save recordings",
                SelectedPath = settings.BasePath
            };

            if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                settings.BasePath = folderDialog.SelectedPath;
                SaveSettings();
            }

            try
            {
                if (!Directory.Exists(settings.BasePath))
                    Directory.CreateDirectory(settings.BasePath);

                string testFile = Path.Combine(settings.BasePath, "test_write.txt");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);

                string fileName = $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
                outputFilePath = Path.Combine(settings.BasePath, fileName);
                tempDir = Path.Combine(settings.BasePath, $"temp_segments_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(tempDir);
                txtOutputPath.Text = outputFilePath;
                System.Windows.MessageBox.Show("Recording will be saved to:\n" + outputFilePath);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error setting output path: " + ex.Message);
            }
        }

        private bool CheckFFmpegAvailability()
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-version",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private bool ValidateAudioDevice(string deviceName, bool useDirectShow = false)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                Debug.WriteLine("Audio device validation failed: Device name is empty.");
                return false;
            }

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                string inputFormat = useDirectShow ? "dshow" : "wasapi"; // Moved outside try block
                try
                {
                    string arguments = useDirectShow
                        ? $"-f dshow -i audio=\"{deviceName}\" -t 1 -f null -"
                        : $"-f wasapi -i audio=\"{deviceName}\" -t 1 -f null -";

                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = arguments,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardError = true,
                            RedirectStandardOutput = true
                        }
                    };

                    StringBuilder errorOutput = new StringBuilder();
                    process.ErrorDataReceived += (s, ev) => { if (ev.Data != null) errorOutput.AppendLine(ev.Data); };
                    process.Start();
                    process.BeginErrorReadLine();
                    process.WaitForExit(5000);

                    if (process.ExitCode != 0)
                    {
                        Debug.WriteLine($"Audio device validation failed for '{deviceName}' (using {inputFormat}, attempt {attempt}): {errorOutput.ToString()}");
                        if (attempt < 3) Thread.Sleep(1000);
                        continue;
                    }
                    Debug.WriteLine($"Audio device validation succeeded for '{deviceName}' (using {inputFormat}).");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error validating audio device '{deviceName}' (using {inputFormat}, attempt {attempt}): {ex.Message}");
                    if (attempt < 3) Thread.Sleep(1000);
                }
            }
            return false;
        }

        private void StartFFmpegRecording()
        {
            string audioSourceType = (cmbAudioSourceType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            string segmentFile = Path.Combine(tempDir, $"segment_{segmentCount}.mp4");
            segmentFiles.Add(segmentFile);
            segmentCount++;

            string ffmpegArgs = null;
            bool useDirectShow = false;

            try
            {
                switch (audioSourceType)
                {
                    case "none":
                        ffmpegArgs = $"-y -f gdigrab -framerate 30 -i desktop -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{segmentFile}\"";
                        break;

                    case "system":
                        if (string.IsNullOrEmpty(systemAudioDevice))
                        {
                            System.Windows.MessageBox.Show($"System audio device is not set.");
                            return;
                        }
                        if (!ValidateAudioDevice(systemAudioDevice))
                        {
                            Debug.WriteLine($"WASAPI validation failed for system audio. Trying DirectShow...");
                            if (ValidateAudioDevice(systemAudioDevice, true))
                            {
                                useDirectShow = true;
                            }
                            else
                            {
                                System.Windows.MessageBox.Show($"System audio device '{systemAudioDevice}' is invalid or not accessible with WASAPI or DirectShow.");
                                return;
                            }
                        }
                        ffmpegArgs = useDirectShow
                            ? $"-y -f dshow -i audio=\"{systemAudioDevice}\" -f gdigrab -framerate 30 -i desktop -c:v libx264 -preset ultrafast -c:a aac -b:a 192k -pix_fmt yuv420p \"{segmentFile}\""
                            : $"-y -f wasapi -i audio=\"{systemAudioDevice}\" -f gdigrab -framerate 30 -i desktop -c:v libx264 -preset ultrafast -c:a aac -b:a 192k -pix_fmt yuv420p \"{segmentFile}\"";
                        break;

                    case "mic":
                        if (string.IsNullOrEmpty(micAudioDevice))
                        {
                            System.Windows.MessageBox.Show($"Microphone device is not set.");
                            return;
                        }
                        if (!ValidateAudioDevice(micAudioDevice))
                        {
                            Debug.WriteLine($"WASAPI validation failed for microphone. Trying DirectShow...");
                            if (ValidateAudioDevice(micAudioDevice, true))
                            {
                                useDirectShow = true;
                            }
                            else
                            {
                                System.Windows.MessageBox.Show($"Microphone device '{micAudioDevice}' is invalid or not accessible with WASAPI or DirectShow.");
                                return;
                            }
                        }
                        ffmpegArgs = useDirectShow
                            ? $"-y -f dshow -i audio=\"{micAudioDevice}\" -f gdigrab -framerate 30 -i desktop -c:v libx264 -preset ultrafast -c:a aac -b:a 192k -pix_fmt yuv420p \"{segmentFile}\""
                            : $"-y -f wasapi -i audio=\"{micAudioDevice}\" -f gdigrab -framerate 30 -i desktop -c:v libx264 -preset ultrafast -c:a aac -b:a 192k -pix_fmt yuv420p \"{segmentFile}\"";
                        break;

                    case "both":
                        if (string.IsNullOrEmpty(systemAudioDevice) || string.IsNullOrEmpty(micAudioDevice))
                        {
                            System.Windows.MessageBox.Show("Both system and microphone devices must be set.");
                            return;
                        }
                        if (!ValidateAudioDevice(systemAudioDevice) || !ValidateAudioDevice(micAudioDevice))
                        {
                            Debug.WriteLine($"WASAPI validation failed for one or both devices. Trying DirectShow...");
                            if (ValidateAudioDevice(systemAudioDevice, true) && ValidateAudioDevice(micAudioDevice, true))
                            {
                                useDirectShow = true;
                            }
                            else
                            {
                                System.Windows.MessageBox.Show($"One or both devices ('{systemAudioDevice}', '{micAudioDevice}') are invalid or not accessible with WASAPI or DirectShow.");
                                return;
                            }
                        }
                        ffmpegArgs = useDirectShow
                            ? $"-y -f dshow -i audio=\"{systemAudioDevice}\" -f dshow -i audio=\"{micAudioDevice}\" -f gdigrab -framerate 30 -i desktop " +
                              "-filter_complex \"[0:a][1:a]amix=inputs=2:duration=first:dropout_transition=3[a]\" -map 2:v -map \"[a]\" " +
                              $"-c:v libx264 -preset ultrafast -c:a aac -b:a 192k -pix_fmt yuv420p \"{segmentFile}\""
                            : $"-y -f wasapi -i audio=\"{systemAudioDevice}\" -f wasapi -i audio=\"{micAudioDevice}\" -f gdigrab -framerate 30 -i desktop " +
                              "-filter_complex \"[0:a][1:a]amix=inputs=2:duration=first:dropout_transition=3[a]\" -map 2:v -map \"[a]\" " +
                              $"-c:v libx264 -preset ultrafast -c:a aac -b:a 192k -pix_fmt yuv420p \"{segmentFile}\"";
                        break;
                }

                ffmpegProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = ffmpegArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = true
                    }
                };

                ffmpegProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) Debug.WriteLine("[FFmpeg Output] " + ev.Data); };
                ffmpegProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) Debug.WriteLine("[FFmpeg Error] " + ev.Data); };

                ffmpegProcess.Start();
                ffmpegProcess.BeginOutputReadLine();
                ffmpegProcess.BeginErrorReadLine();
                Thread.Sleep(2000);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to start recording: " + ex.Message);
                ffmpegProcess = null;
            }
        }

        private void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                System.Windows.MessageBox.Show("Please select an output path before recording.");
                return;
            }

            if (!CheckFFmpegAvailability())
            {
                System.Windows.MessageBox.Show("FFmpeg is not installed or not found in PATH. Please install FFmpeg and ensure it's accessible.");
                return;
            }

            segmentFiles.Clear();
            segmentCount = 0;

            try
            {
                StartFFmpegRecording();
                if (ffmpegProcess == null) return;

                startTime = DateTime.Now;
                pausedTime = TimeSpan.Zero;
                isPaused = false;
                timer.Start();
                txtElapsed.Text = "Time Elapsed: 00:00:00";

                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;
                btnPause.IsEnabled = true;
                btnPause.Content = "⏸ Pause";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Failed to initialize recording: " + ex.Message);
            }
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (ffmpegProcess == null || ffmpegProcess.HasExited) return;

            try
            {
                if (!isPaused)
                {
                    ffmpegProcess.StandardInput.WriteLine("q");
                    ffmpegProcess.WaitForExit(10000);
                    if (!ffmpegProcess.HasExited)
                    {
                        ffmpegProcess.Kill();
                    }
                    ffmpegProcess.Dispose();
                    ffmpegProcess = null;

                    string lastSegment = segmentFiles[segmentFiles.Count - 1];
                    TimeSpan segmentDuration = DateTime.Now - startTime;
                    if (!File.Exists(lastSegment) || new FileInfo(lastSegment).Length == 0 || segmentDuration.TotalSeconds < 2)
                    {
                        Debug.WriteLine($"Invalid segment file: {lastSegment} (Duration: {segmentDuration.TotalSeconds}s, Size: {new FileInfo(lastSegment).Length} bytes)");
                        segmentFiles.RemoveAt(segmentFiles.Count - 1);
                        segmentCount--;
                    }

                    timer.Stop();
                    pausedTime += DateTime.Now - startTime;
                    isPaused = true;
                    btnPause.Content = "▶ Resume";
                }
                else
                {
                    StartFFmpegRecording();
                    if (ffmpegProcess == null) return;

                    startTime = DateTime.Now;
                    timer.Start();
                    isPaused = false;
                    btnPause.Content = "⏸ Pause";
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error during pause/resume: " + ex.Message);
            }
        }

        private void MergeSegments()
        {
            if (segmentFiles.Count == 0)
            {
                Debug.WriteLine("No segments to merge.");
                return;
            }

            for (int i = segmentFiles.Count - 1; i >= 0; i--)
            {
                if (!File.Exists(segmentFiles[i]) || new FileInfo(segmentFiles[i]).Length == 0)
                {
                    Debug.WriteLine($"Invalid or missing segment file: {segmentFiles[i]}");
                    segmentFiles.RemoveAt(i);
                }
            }

            if (segmentFiles.Count == 0)
            {
                Debug.WriteLine("No valid segments found for merging.");
                return;
            }

            string concatFile = Path.Combine(tempDir, "concat.txt");
            StringBuilder concatContent = new StringBuilder();
            foreach (string segment in segmentFiles)
            {
                string escapedSegment = segment.Replace("'", "'\\''").Replace("\\", "/");
                concatContent.AppendLine($"file '{escapedSegment}'");
            }

            Process mergeProcess = null;
            try
            {
                File.WriteAllText(concatFile, concatContent.ToString());
                Debug.WriteLine($"Concat file content:\n{concatContent.ToString()}");

                string ffmpegArgs = $"-y -f concat -safe 0 -i \"{concatFile}\" -c copy \"{outputFilePath}\"";
                mergeProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = ffmpegArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };

                StringBuilder errorOutput = new StringBuilder();
                mergeProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) Debug.WriteLine("[FFmpeg Merge Output] " + ev.Data); };
                mergeProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) errorOutput.AppendLine(ev.Data); };

                mergeProcess.Start();
                mergeProcess.BeginOutputReadLine();
                mergeProcess.BeginErrorReadLine();
                mergeProcess.WaitForExit(15000);

                if (mergeProcess.ExitCode != 0)
                {
                    System.Windows.MessageBox.Show($"Error merging video segments: {errorOutput.ToString()}");
                    return;
                }

                if (!File.Exists(outputFilePath) || new FileInfo(outputFilePath).Length == 0)
                {
                    System.Windows.MessageBox.Show("Merged output file is invalid or empty.");
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Error merging segments: " + ex.Message);
            }
            finally
            {
                if (mergeProcess != null)
                {
                    mergeProcess.Dispose();
                }
            }
        }

        private void CleanUpTempFiles()
        {
            if (string.IsNullOrEmpty(tempDir) || !Directory.Exists(tempDir))
            {
                Debug.WriteLine("No temporary directory to clean up.");
                return;
            }

            try
            {
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        Directory.Delete(tempDir, true);
                        Debug.WriteLine($"Successfully deleted temporary directory: {tempDir}");
                        return;
                    }
                    catch (IOException ex)
                    {
                        Debug.WriteLine($"Attempt {attempt} failed to delete temp directory: {ex.Message}");
                        if (attempt < 3)
                            Thread.Sleep(1000);
                    }
                }
                Debug.WriteLine($"Failed to delete temporary directory after 3 attempts: {tempDir}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error deleting temp directory: {ex.Message}");
            }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                try
                {
                    ffmpegProcess.StandardInput.WriteLine("q");
                    ffmpegProcess.WaitForExit(10000);
                    if (!ffmpegProcess.HasExited)
                    {
                        ffmpegProcess.Kill();
                    }
                    ffmpegProcess.Dispose();
                    ffmpegProcess = null;

                    if (segmentFiles.Count > 0)
                    {
                        string lastSegment = segmentFiles[segmentFiles.Count - 1];
                        TimeSpan segmentDuration = DateTime.Now - startTime + pausedTime;
                        if (!File.Exists(lastSegment) || new FileInfo(lastSegment).Length == 0 || segmentDuration.TotalSeconds < 2)
                        {
                            Debug.WriteLine($"Invalid segment file: {lastSegment} (Duration: {segmentDuration.TotalSeconds}s, Size: {new FileInfo(lastSegment).Length} bytes)");
                            segmentFiles.RemoveAt(segmentFiles.Count - 1);
                            segmentCount--;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show("Error stopping recording: " + ex.Message);
                }
            }

            timer.Stop();
            txtElapsed.Text = "Time Elapsed: 00:00:00";

            MergeSegments();

            bool isVideoSaved = File.Exists(outputFilePath) && new FileInfo(outputFilePath).Length > 0;
            if (isVideoSaved)
            {
                long fileSize = new FileInfo(outputFilePath).Length;
                Debug.WriteLine($"Output video saved: {outputFilePath} (Size: {fileSize} bytes)");
                System.Windows.MessageBox.Show($"Recording saved to:\n{Path.GetFullPath(outputFilePath)}", "Done");
            }
            else
            {
                System.Windows.MessageBox.Show("Recording failed to save.", "Error");
            }

            CleanUpTempFiles();

            btnStart.IsEnabled = true;
            btnStop.IsEnabled = false;
            btnPause.IsEnabled = false;
            btnPause.Content = "⏸ Pause";
            isPaused = false;
            pausedTime = TimeSpan.Zero;
            segmentFiles.Clear();
            segmentCount = 0;
            tempDir = null;
        }

        private void cmbAudioSourceType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cmbAudioDevices == null)
                return;

            var selectedTag = (cmbAudioSourceType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            cmbAudioDevices.IsEnabled = selectedTag == "system" || selectedTag == "mic" || selectedTag == "both";
            SaveSettings();
        }
    }

    public class AppSettings
    {
        public string BasePath { get; set; }
        public string AudioSourceType { get; set; }
        public string SelectedDevice { get; set; }
    }
}