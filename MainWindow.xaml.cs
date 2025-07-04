using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        private List<string> videoSegmentFiles;
        private List<string> audioSegmentFiles;
        private DispatcherTimer timer;
        private DateTime startTime;
        private TimeSpan pausedTime;
        private bool isPaused;
        private string systemAudioDevice = null;
        private string micAudioDevice = null;
        private int segmentCount;
        private WasapiCapture micCapture;
        private WasapiLoopbackCapture desktopCapture;
        private WaveFileWriter audioWriter;
        private BufferedWaveProvider micBuffer;
        private BufferedWaveProvider desktopBuffer;
        private CancellationTokenSource audioCts;
        private DateTime audioCaptureStartTime;
        private DateTime firstAudioSampleTime;
        private bool isAudioSignificant;

        private AppSettings settings;
        private readonly string settingsFile = "settings.json";
        private const float MicGain = 1.0f;
        private const float DesktopGain = 1.2f;
        private readonly WaveFormat targetFormat = new WaveFormat(48000, 16, 2);

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += Timer_Tick;
            videoSegmentFiles = new List<string>();
            audioSegmentFiles = new List<string>();
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
                    AudioSourceType = "none",
                    SystemAudioDevice = null,
                    MicAudioDevice = null
                };
            }
        }

        private void SaveSettings()
        {
            settings.AudioSourceType = (cmbAudioSourceType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            settings.SystemAudioDevice = systemAudioDevice;
            settings.MicAudioDevice = micAudioDevice;
            File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void LoadAudioDevices()
        {
            var enumerator = new MMDeviceEnumerator();
            var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            try
            {
                var defaultOut = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                systemAudioDevice = defaultOut.FriendlyName;
                Debug.WriteLine($"System audio device detected: {systemAudioDevice}");

                var defaultMic = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                micAudioDevice = defaultMic.FriendlyName;
                Debug.WriteLine($"Microphone device detected: {micAudioDevice}");

                var allDevices = new List<string>();
                foreach (var device in renderDevices)
                    allDevices.Add(device.FriendlyName);
                foreach (var device in captureDevices)
                    if (!allDevices.Contains(device.FriendlyName))
                        allDevices.Add(device.FriendlyName);

                cmbAudioDevices.ItemsSource = allDevices;

                if (!string.IsNullOrEmpty(settings.SystemAudioDevice) && allDevices.Contains(settings.SystemAudioDevice))
                    systemAudioDevice = settings.SystemAudioDevice;
                if (!string.IsNullOrEmpty(settings.MicAudioDevice) && allDevices.Contains(settings.MicAudioDevice))
                    micAudioDevice = settings.MicAudioDevice;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error detecting audio devices: {ex.Message}");
                System.Windows.MessageBox.Show($"Error detecting audio devices: {ex.Message}\nPlease select devices manually.");
            }
        }

        private MMDevice FindDevice(string deviceName, DataFlow flow)
        {
            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            foreach (var device in devices)
            {
                if (device.FriendlyName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
                    return device;
            }
            return null;
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

        private void StartFFmpegRecording()
        {
            string videoSegmentFile = Path.Combine(tempDir, $"video_segment_{segmentCount}.mp4");
            videoSegmentFiles.Add(videoSegmentFile);

            string ffmpegArgs = $"-y -f gdigrab -framerate 30 -re -rtbufsize 200M -use_wallclock_as_timestamps 1 -fflags +genpts -i desktop -c:v libx264 -preset ultrafast -pix_fmt yuv420p -metadata:s:v creation_time=now \"{videoSegmentFile}\"";
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
            Debug.WriteLine($"Started video recording: {videoSegmentFile}, Start: {DateTime.Now:O}");
        }

        private async Task StartAudioRecordingAsync(string audioSourceType, CancellationToken token)
        {
            if (audioSourceType == "none")
                return;

            string audioSegmentFile = Path.Combine(tempDir, $"audio_segment_{segmentCount}.wav");
            audioSegmentFiles.Add(audioSegmentFile);

            MMDevice micDevice = null, desktopDevice = null;
            if (audioSourceType == "mic" || audioSourceType == "both")
            {
                micDevice = FindDevice(micAudioDevice, DataFlow.Capture);
                if (micDevice == null)
                {
                    System.Windows.MessageBox.Show($"Microphone device '{micAudioDevice}' not found.");
                    return;
                }
                if (micDevice.State != DeviceState.Active)
                {
                    System.Windows.MessageBox.Show($"Microphone device '{micAudioDevice}' is not active.");
                    return;
                }
            }
            if (audioSourceType == "system" || audioSourceType == "both")
            {
                desktopDevice = FindDevice(systemAudioDevice, DataFlow.Render);
                if (desktopDevice == null)
                {
                    System.Windows.MessageBox.Show($"System audio device '{systemAudioDevice}' not found.");
                    return;
                }
                if (desktopDevice.State != DeviceState.Active)
                {
                    System.Windows.MessageBox.Show($"System audio device '{systemAudioDevice}' is not active.");
                    return;
                }
            }

            try
            {
                audioCaptureStartTime = DateTime.Now;
                firstAudioSampleTime = DateTime.MaxValue;
                isAudioSignificant = false;
                Debug.WriteLine($"Audio recording initiated at: {audioCaptureStartTime:O}");

                if (audioSourceType == "mic")
                {
                    micCapture = new WasapiCapture(micDevice) { WaveFormat = targetFormat, ShareMode = AudioClientShareMode.Shared };
                    micBuffer = new BufferedWaveProvider(micCapture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = false,
                        BufferDuration = TimeSpan.FromSeconds(2),
                        ReadFully = false
                    };
                    audioWriter = new WaveFileWriter(audioSegmentFile, micCapture.WaveFormat);

                    micCapture.DataAvailable += (s, a) =>
                    {
                        float maxAmplitude = 0f;
                        for (int i = 0; i < a.BytesRecorded / 2; i += 2)
                        {
                            short sample = BitConverter.ToInt16(a.Buffer, i);
                            maxAmplitude = Math.Max(maxAmplitude, Math.Abs(sample / 32768f));
                        }
                        if (maxAmplitude > 0.02f && firstAudioSampleTime == DateTime.MaxValue)
                        {
                            firstAudioSampleTime = DateTime.Now;
                            isAudioSignificant = true;
                            Debug.WriteLine($"First non-silent audio sample at {firstAudioSampleTime:O}, {(firstAudioSampleTime - audioCaptureStartTime).TotalSeconds:F2}s after start");
                        }
                        Debug.WriteLine($"Microphone data received at {DateTime.Now:O}: {a.BytesRecorded} bytes, Max amplitude: {maxAmplitude:F3}");
                        if (isAudioSignificant)
                            micBuffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
                    };

                    micCapture.StartRecording();
                    Debug.WriteLine($"Started microphone recording: {micDevice.FriendlyName}, Format: {micCapture.WaveFormat}, Start: {audioCaptureStartTime:O}");

                    Task.Run(() =>
                    {
                        byte[] buffer = new byte[micBuffer.WaveFormat.AverageBytesPerSecond / 10];
                        while (!token.IsCancellationRequested)
                        {
                            try
                            {
                                while (micBuffer.BufferedBytes < buffer.Length && !token.IsCancellationRequested)
                                {
                                    Debug.WriteLine($"Waiting for mic buffer: {micBuffer.BufferedBytes} bytes");
                                    Thread.Sleep(10);
                                }
                                if (isAudioSignificant)
                                {
                                    int bytesRead = micBuffer.Read(buffer, 0, buffer.Length);
                                    if (bytesRead > 0)
                                    {
                                        audioWriter.Write(buffer, 0, bytesRead);
                                        Debug.WriteLine($"Wrote {bytesRead} bytes from mic buffer to audio file");
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"No mic buffer data available: {micBuffer.BufferedBytes} bytes");
                                        Thread.Sleep(50);
                                    }
                                }
                                else
                                {
                                    Thread.Sleep(50);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error in mic buffer reading loop: {ex.Message}");
                            }
                        }
                    }, token);
                }
                else if (audioSourceType == "system")
                {
                    desktopCapture = new WasapiLoopbackCapture(desktopDevice) { WaveFormat = targetFormat, ShareMode = AudioClientShareMode.Shared };
                    desktopBuffer = new BufferedWaveProvider(desktopCapture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = false,
                        BufferDuration = TimeSpan.FromSeconds(2),
                        ReadFully = false
                    };
                    audioWriter = new WaveFileWriter(audioSegmentFile, desktopCapture.WaveFormat);

                    desktopCapture.DataAvailable += (s, a) =>
                    {
                        float maxAmplitude = 0f;
                        for (int i = 0; i < a.BytesRecorded / 2; i += 2)
                        {
                            short sample = BitConverter.ToInt16(a.Buffer, i);
                            maxAmplitude = Math.Max(maxAmplitude, Math.Abs(sample / 32768f));
                        }
                        if (maxAmplitude > 0.02f && firstAudioSampleTime == DateTime.MaxValue)
                        {
                            firstAudioSampleTime = DateTime.Now;
                            isAudioSignificant = true;
                            Debug.WriteLine($"First non-silent audio sample at {firstAudioSampleTime:O}, {(firstAudioSampleTime - audioCaptureStartTime).TotalSeconds:F2}s after start");
                        }
                        Debug.WriteLine($"Desktop data received at {DateTime.Now:O}: {a.BytesRecorded} bytes, Max amplitude: {maxAmplitude:F3}");
                        if (isAudioSignificant)
                            desktopBuffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
                    };

                    desktopCapture.StartRecording();
                    Debug.WriteLine($"Started desktop audio recording: {desktopDevice.FriendlyName}, Format: {desktopCapture.WaveFormat}, Start: {audioCaptureStartTime:O}");

                    Task.Run(() =>
                    {
                        byte[] buffer = new byte[desktopBuffer.WaveFormat.AverageBytesPerSecond / 10];
                        while (!token.IsCancellationRequested)
                        {
                            try
                            {
                                while (desktopBuffer.BufferedBytes < buffer.Length && !token.IsCancellationRequested)
                                {
                                    Debug.WriteLine($"Waiting for desktop buffer: {desktopBuffer.BufferedBytes} bytes");
                                    Thread.Sleep(10);
                                }
                                if (isAudioSignificant)
                                {
                                    int bytesRead = desktopBuffer.Read(buffer, 0, buffer.Length);
                                    if (bytesRead > 0)
                                    {
                                        audioWriter.Write(buffer, 0, bytesRead);
                                        Debug.WriteLine($"Wrote {bytesRead} bytes from desktop buffer to audio file");
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"No desktop buffer data available: {desktopBuffer.BufferedBytes} bytes");
                                        Thread.Sleep(50);
                                    }
                                }
                                else
                                {
                                    Thread.Sleep(50);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Error in desktop buffer reading loop: {ex.Message}");
                            }
                        }
                    }, token);
                }
                else if (audioSourceType == "both")
                {
                    micCapture = new WasapiCapture(micDevice) { WaveFormat = targetFormat, ShareMode = AudioClientShareMode.Shared };
                    desktopCapture = new WasapiLoopbackCapture(desktopDevice) { WaveFormat = targetFormat, ShareMode = AudioClientShareMode.Shared };
                    micBuffer = new BufferedWaveProvider(micCapture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = false,
                        BufferDuration = TimeSpan.FromSeconds(2),
                        ReadFully = false
                    };
                    desktopBuffer = new BufferedWaveProvider(desktopCapture.WaveFormat)
                    {
                        DiscardOnBufferOverflow = false,
                        BufferDuration = TimeSpan.FromSeconds(2),
                        ReadFully = false
                    };
                    audioWriter = new WaveFileWriter(audioSegmentFile, targetFormat);

                    micCapture.DataAvailable += (s, a) =>
                    {
                        float maxAmplitude = 0f;
                        for (int i = 0; i < a.BytesRecorded / 2; i += 2)
                        {
                            short sample = BitConverter.ToInt16(a.Buffer, i);
                            maxAmplitude = Math.Max(maxAmplitude, Math.Abs(sample / 32768f));
                        }
                        if (maxAmplitude > 0.02f && firstAudioSampleTime == DateTime.MaxValue)
                        {
                            firstAudioSampleTime = DateTime.Now;
                            isAudioSignificant = true;
                            Debug.WriteLine($"First non-silent audio sample at {firstAudioSampleTime:O}, {(firstAudioSampleTime - audioCaptureStartTime).TotalSeconds:F2}s after start");
                        }
                        Debug.WriteLine($"Microphone data received at {DateTime.Now:O}: {a.BytesRecorded} bytes, Max amplitude: {maxAmplitude:F3}");
                        if (isAudioSignificant)
                            micBuffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
                    };

                    desktopCapture.DataAvailable += (s, a) =>
                    {
                        float maxAmplitude = 0f;
                        for (int i = 0; i < a.BytesRecorded / 2; i += 2)
                        {
                            short sample = BitConverter.ToInt16(a.Buffer, i);
                            maxAmplitude = Math.Max(maxAmplitude, Math.Abs(sample / 32768f));
                        }
                        if (maxAmplitude > 0.02f && firstAudioSampleTime == DateTime.MaxValue)
                        {
                            firstAudioSampleTime = DateTime.Now;
                            isAudioSignificant = true;
                            Debug.WriteLine($"First non-silent audio sample at {firstAudioSampleTime:O}, {(firstAudioSampleTime - audioCaptureStartTime).TotalSeconds:F2}s after start");
                        }
                        Debug.WriteLine($"Desktop data received at {DateTime.Now:O}: {a.BytesRecorded} bytes, Max amplitude: {maxAmplitude:F3}");
                        if (isAudioSignificant)
                            desktopBuffer.AddSamples(a.Buffer, 0, a.BytesRecorded);
                    };

                    micCapture.StartRecording();
                    desktopCapture.StartRecording();
                    Debug.WriteLine($"Started microphone recording: {micDevice.FriendlyName}, Format: {micCapture.WaveFormat}, Start: {audioCaptureStartTime:O}");
                    Debug.WriteLine($"Started desktop audio recording: {desktopDevice.FriendlyName}, Format: {desktopCapture.WaveFormat}, Start: {audioCaptureStartTime:O}");

                    var micProvider = ConvertToSampleProvider(micBuffer.ToSampleProvider(), targetFormat);
                    var desktopProvider = ConvertToSampleProvider(desktopBuffer.ToSampleProvider(), targetFormat);

                    float noiseGateThreshold = 0.02f;
                    var floatBuffer = new float[targetFormat.SampleRate * targetFormat.Channels / 100];
                    var micSamples = new float[floatBuffer.Length];
                    var desktopSamples = new float[floatBuffer.Length];

                    while (!token.IsCancellationRequested)
                    {
                        try
                        {
                            while ((micBuffer.BufferedBytes < 3840 || desktopBuffer.BufferedBytes < 3840) && !token.IsCancellationRequested)
                            {
                                Debug.WriteLine($"Waiting for buffer - Mic: {micBuffer.BufferedBytes} bytes, Desktop: {desktopBuffer.BufferedBytes} bytes");
                                await Task.Delay(10, token);
                            }
                            if (isAudioSignificant)
                            {
                                int micRead = micProvider.Read(micSamples, 0, floatBuffer.Length);
                                int desktopRead = desktopProvider.Read(desktopSamples, 0, floatBuffer.Length);
                                Debug.WriteLine($"Individual reads - Mic: {micRead} samples, Desktop: {desktopRead} samples");

                                float maxMicAmplitude = 0f;
                                float maxDesktopAmplitude = 0f;
                                for (int i = 0; i < micRead; i++)
                                    maxMicAmplitude = Math.Max(maxMicAmplitude, Math.Abs(micSamples[i]));
                                for (int i = 0; i < desktopRead; i++)
                                    maxDesktopAmplitude = Math.Max(maxDesktopAmplitude, Math.Abs(desktopSamples[i]));
                                Debug.WriteLine($"Max amplitudes - Mic: {maxMicAmplitude:F3}, Desktop: {maxDesktopAmplitude:F3}");

                                int samplesToMix = Math.Min(micRead, desktopRead);
                                if (samplesToMix > 0)
                                {
                                    for (int i = 0; i < samplesToMix; i++)
                                    {
                                        float micSample = Math.Abs(micSamples[i]) > noiseGateThreshold ? micSamples[i] : 0f;
                                        float desktopSample = Math.Abs(desktopSamples[i]) > noiseGateThreshold ? desktopSamples[i] : 0f;
                                        float mixedSample = (MicGain * micSample + DesktopGain * desktopSample) / 2.0f;
                                        floatBuffer[i] = Math.Max(-1.0f, Math.Min(1.0f, mixedSample));
                                    }
                                    audioWriter.WriteSamples(floatBuffer, 0, samplesToMix);
                                    Debug.WriteLine($"Mixed {samplesToMix} samples, Microphone buffer: {micBuffer.BufferedBytes} bytes, Desktop buffer: {desktopBuffer.BufferedBytes} bytes");
                                }
                                else
                                {
                                    Debug.WriteLine($"No samples available (Mic: {micRead}, Desktop: {desktopRead}), Microphone buffer: {micBuffer.BufferedBytes} bytes, Desktop buffer: {desktopBuffer.BufferedBytes} bytes");
                                    await Task.Delay(50, token);
                                }
                            }
                            else
                            {
                                await Task.Delay(50, token);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error in mixing loop: {ex.Message}");
                        }
                    }
                }

                // Add timestamp to audio file metadata
                if (isAudioSignificant && File.Exists(audioSegmentFile) && new FileInfo(audioSegmentFile).Length > 0)
                {
                    string tempAudioFile = audioSegmentFile + ".temp.wav";
                    double audioOffset = (firstAudioSampleTime != DateTime.MaxValue) ? (firstAudioSampleTime - audioCaptureStartTime).TotalSeconds : 0;
                    string ffmpegArgs = $"-y -i \"{audioSegmentFile}\" -c copy -metadata:s:a creation_time=\"{audioCaptureStartTime:O}\" -metadata:s:a start_time=\"{audioOffset}\" \"{tempAudioFile}\"";
                    var process = new Process
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
                    process.Start();
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                    {
                        File.Delete(audioSegmentFile);
                        File.Move(tempAudioFile, audioSegmentFile);
                        Debug.WriteLine($"Added timestamp to audio segment: {audioSegmentFile}, creation_time={audioCaptureStartTime:O}, start_time={audioOffset:F2}s");
                    }
                    else
                    {
                        Debug.WriteLine($"Failed to add timestamp to audio segment: {audioSegmentFile}");
                    }
                    process.Dispose();

                    // Log audio segment info
                    string ffprobeArgs = $"-i \"{audioSegmentFile}\" -show_streams -print_format json";
                    var audioProbeProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "ffprobe",
                            Arguments = ffprobeArgs,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    audioProbeProcess.Start();
                    string audioOutput = audioProbeProcess.StandardOutput.ReadToEnd();
                    audioProbeProcess.WaitForExit();
                    Debug.WriteLine($"Audio segment {audioSegmentFile} info: {audioOutput}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Audio recording error: {ex.Message}");
                throw;
            }
        }

        private async Task StopAudioRecordingAsync()
        {
            if (micCapture != null)
            {
                micCapture.StopRecording();
                micCapture.Dispose();
                micCapture = null;
            }
            if (desktopCapture != null)
            {
                desktopCapture.StopRecording();
                desktopCapture.Dispose();
                desktopCapture = null;
            }
            if (audioWriter != null)
            {
                // Flush buffered samples
                if (micBuffer != null && micBuffer.BufferedBytes > 0 && isAudioSignificant)
                {
                    byte[] buffer = new byte[micBuffer.BufferedBytes];
                    micBuffer.Read(buffer, 0, buffer.Length);
                    audioWriter.Write(buffer, 0, buffer.Length);
                    Debug.WriteLine($"Flushed {buffer.Length} bytes from mic buffer");
                }
                if (desktopBuffer != null && desktopBuffer.BufferedBytes > 0 && isAudioSignificant)
                {
                    byte[] buffer = new byte[desktopBuffer.BufferedBytes];
                    desktopBuffer.Read(buffer, 0, buffer.Length);
                    audioWriter.Write(buffer, 0, buffer.Length);
                    Debug.WriteLine($"Flushed {buffer.Length} bytes from desktop buffer");
                }

                // Pad audio to match video duration
                if (videoSegmentFiles.Count > 0 && isAudioSignificant)
                {
                    string videoSegment = videoSegmentFiles[videoSegmentFiles.Count - 1];
                    double videoDuration = GetMediaDuration(videoSegment);
                    double audioDuration = audioWriter.Length / (double)audioWriter.WaveFormat.AverageBytesPerSecond;
                    double audioOffset = (firstAudioSampleTime != DateTime.MaxValue) ? (firstAudioSampleTime - audioCaptureStartTime).TotalSeconds : 0;
                    if (audioDuration + audioOffset < videoDuration)
                    {
                        byte[] silenceBuffer = new byte[(int)((videoDuration - audioDuration - audioOffset) * audioWriter.WaveFormat.AverageBytesPerSecond)];
                        Array.Clear(silenceBuffer, 0, silenceBuffer.Length);
                        audioWriter.Write(silenceBuffer, 0, silenceBuffer.Length);
                        Debug.WriteLine($"Padded audio with {(videoDuration - audioDuration - audioOffset):F2}s of silence to match video duration {videoDuration:F2}s");
                    }
                }
                audioWriter.Flush();
                audioWriter.Dispose();
                audioWriter = null;
            }
            micBuffer = null;
            desktopBuffer = null;
            isAudioSignificant = false;
        }

        private double GetMediaDuration(string filePath)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffprobe",
                        Arguments = $"-i \"{filePath}\" -show_streams -print_format json",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                using (JsonDocument doc = JsonDocument.Parse(output))
                {
                    var streams = doc.RootElement.GetProperty("streams");
                    foreach (var stream in streams.EnumerateArray())
                    {
                        if (stream.TryGetProperty("duration", out var durationElement))
                        {
                            if (double.TryParse(durationElement.GetString(), out double duration))
                                return duration;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting duration for {filePath}: {ex.Message}");
            }
            return 0;
        }

        private static ISampleProvider ConvertToSampleProvider(ISampleProvider sample, WaveFormat target)
        {
            Debug.WriteLine($"Converting sample provider format: {sample.WaveFormat} to target: {target}");
            if (sample.WaveFormat.Channels == 1)
            {
                Debug.WriteLine("Converting mono to stereo");
                sample = new MonoToStereoSampleProvider(sample);
            }
            if (sample.WaveFormat.SampleRate != target.SampleRate ||
                sample.WaveFormat.Channels != target.Channels)
            {
                Debug.WriteLine($"Resampling to {target.SampleRate} Hz");
                sample = new WdlResamplingSampleProvider(sample, target.SampleRate);
            }
            return sample;
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

            videoSegmentFiles.Clear();
            audioSegmentFiles.Clear();
            segmentCount = 0;

            try
            {
                string audioSourceType = (cmbAudioSourceType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                audioCts = new CancellationTokenSource();
                startTime = DateTime.Now;

                if (audioSourceType != "none")
                {
                    Task audioTask = Task.Run(() => StartAudioRecordingAsync(audioSourceType, audioCts.Token));
                }

                StartFFmpegRecording();
                if (ffmpegProcess == null) return;

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

        private async void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (ffmpegProcess == null || ffmpegProcess.HasExited) return;

            try
            {
                if (!isPaused)
                {
                    ffmpegProcess.StandardInput.WriteLine("q");
                    ffmpegProcess.WaitForExit(15000);
                    if (!ffmpegProcess.HasExited)
                    {
                        ffmpegProcess.Kill();
                    }
                    ffmpegProcess.Dispose();
                    ffmpegProcess = null;

                    audioCts?.Cancel();
                    await StopAudioRecordingAsync();

                    string lastVideoSegment = videoSegmentFiles[videoSegmentFiles.Count - 1];
                    TimeSpan segmentDuration = DateTime.Now - startTime;
                    if (!File.Exists(lastVideoSegment) || new FileInfo(lastVideoSegment).Length == 0 || segmentDuration.TotalSeconds < 0.5)
                    {
                        Debug.WriteLine($"Invalid video segment file: {lastVideoSegment} (Duration: {segmentDuration.TotalSeconds}s, Size: {new FileInfo(lastVideoSegment).Length} bytes)");
                        videoSegmentFiles.RemoveAt(videoSegmentFiles.Count - 1);
                        if (audioSegmentFiles.Count > 0)
                        {
                            string lastAudioSegment = audioSegmentFiles[audioSegmentFiles.Count - 1];
                            if (!File.Exists(lastAudioSegment) || new FileInfo(lastAudioSegment).Length == 0)
                            {
                                Debug.WriteLine($"Invalid audio segment file: {lastAudioSegment} (Size: {new FileInfo(lastAudioSegment).Length} bytes)");
                                audioSegmentFiles.RemoveAt(audioSegmentFiles.Count - 1);
                            }
                        }
                        segmentCount--;
                    }

                    timer.Stop();
                    pausedTime += DateTime.Now - startTime;
                    isPaused = true;
                    btnPause.Content = "▶ Resume";
                }
                else
                {
                    segmentCount++;
                    audioCts = new CancellationTokenSource();
                    startTime = DateTime.Now;

                    string audioSourceType = (cmbAudioSourceType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                    if (audioSourceType != "none")
                    {
                        Task audioTask = Task.Run(() => StartAudioRecordingAsync(audioSourceType, audioCts.Token));
                    }

                    StartFFmpegRecording();
                    if (ffmpegProcess == null) return;

                    timer.Start();
                    isPaused = false;
                    btnPause.Content = "⏸ Pause";
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error during pause/resume: {ex.Message}");
            }
        }

        private void MergeSegments()
        {
            if (videoSegmentFiles.Count == 0)
            {
                Debug.WriteLine("No video segments to merge.");
                return;
            }

            for (int i = videoSegmentFiles.Count - 1; i >= 0; i--)
            {
                if (!File.Exists(videoSegmentFiles[i]) || new FileInfo(videoSegmentFiles[i]).Length == 0)
                {
                    Debug.WriteLine($"Invalid or missing video segment file: {videoSegmentFiles[i]}");
                    videoSegmentFiles.RemoveAt(i);
                    if (i < audioSegmentFiles.Count)
                    {
                        Debug.WriteLine($"Removing corresponding audio segment file: {audioSegmentFiles[i]}");
                        audioSegmentFiles.RemoveAt(i);
                    }
                }
                else
                {
                    string ffprobeArgs = $"-i \"{videoSegmentFiles[i]}\" -show_streams -print_format json";
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "ffprobe",
                            Arguments = ffprobeArgs,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    Debug.WriteLine($"Video segment {videoSegmentFiles[i]} info: {output}");
                }
            }

            if (videoSegmentFiles.Count == 0)
            {
                Debug.WriteLine("No valid video segments found for merging.");
                return;
            }

            string concatFile = Path.Combine(tempDir, "concat.txt");
            string tempAudioFile = Path.Combine(tempDir, "merged_audio.wav");
            string tempVideoFile = Path.Combine(tempDir, "merged_video.mp4");

            Process mergeProcess = null;
            try
            {
                // Merge video segments
                StringBuilder concatContent = new StringBuilder();
                foreach (string segment in videoSegmentFiles)
                {
                    string escapedSegment = segment.Replace("'", "'\\''").Replace("\\", "/");
                    concatContent.AppendLine($"file '{escapedSegment}'");
                }
                File.WriteAllText(concatFile, concatContent.ToString());
                Debug.WriteLine($"Video concat file content:\n{concatContent.ToString()}");

                string videoMergeArgs = $"-y -f concat -safe 0 -i \"{concatFile}\" -c copy -fflags +genpts \"{tempVideoFile}\"";
                mergeProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = videoMergeArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    }
                };

                StringBuilder errorOutput = new StringBuilder();
                mergeProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) Debug.WriteLine("[FFmpeg Video Merge Output] " + ev.Data); };
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

                if (!File.Exists(tempVideoFile) || new FileInfo(tempVideoFile).Length == 0)
                {
                    System.Windows.MessageBox.Show("Merged video file is invalid or empty.");
                    return;
                }

                // Merge audio segments if any
                if (audioSegmentFiles.Count > 0)
                {
                    try
                    {
                        double videoDuration = GetMediaDuration(tempVideoFile);
                        double audioOffset = (firstAudioSampleTime != DateTime.MaxValue) ? (firstAudioSampleTime - audioCaptureStartTime).TotalSeconds : 0;
                        using (var outputWaveFile = new WaveFileWriter(tempAudioFile, targetFormat))
                        {
                            foreach (string audioSegment in audioSegmentFiles)
                            {
                                if (!File.Exists(audioSegment))
                                {
                                    Debug.WriteLine($"Audio segment file missing: {audioSegment}");
                                    continue;
                                }

                                using (var reader = new WaveFileReader(audioSegment))
                                {
                                    var provider = ConvertToSampleProvider(reader.ToSampleProvider(), targetFormat);
                                    var buffer = new float[targetFormat.SampleRate * targetFormat.Channels / 100];
                                    int samplesRead;
                                    while ((samplesRead = provider.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        outputWaveFile.WriteSamples(buffer, 0, samplesRead);
                                    }
                                }
                            }
                            // Pad audio to match video duration
                            double audioDuration = outputWaveFile.Length / (double)outputWaveFile.WaveFormat.AverageBytesPerSecond;
                            if (audioDuration + audioOffset < videoDuration)
                            {
                                byte[] silenceBuffer = new byte[(int)((videoDuration - audioDuration - audioOffset) * outputWaveFile.WaveFormat.AverageBytesPerSecond)];
                                Array.Clear(silenceBuffer, 0, silenceBuffer.Length);
                                outputWaveFile.Write(silenceBuffer, 0, silenceBuffer.Length);
                                Debug.WriteLine($"Padded merged audio with {(videoDuration - audioDuration - audioOffset):F2}s of silence to match video duration {videoDuration:F2}s");
                            }
                            outputWaveFile.Flush();
                        }

                        string ffprobeArgs = $"-i \"{tempAudioFile}\" -show_streams -print_format json";
                        var audioProbeProcess = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "ffprobe",
                                Arguments = ffprobeArgs,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true
                            }
                        };
                        audioProbeProcess.Start();
                        string audioOutput = audioProbeProcess.StandardOutput.ReadToEnd();
                        audioProbeProcess.WaitForExit();
                        Debug.WriteLine($"Merged audio file info: {audioOutput}");

                        string finalMergeArgs = $"-y -i \"{tempVideoFile}\" -itsoffset {audioOffset:F2} -i \"{tempAudioFile}\" -c:v copy -c:a aac -b:a 192k -map 0:v -map 1:a -async 1 -fflags +genpts \"{outputFilePath}\"";
                        mergeProcess = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = "ffmpeg",
                                Arguments = finalMergeArgs,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardError = true,
                                RedirectStandardOutput = true
                            }
                        };

                        errorOutput = new StringBuilder();
                        mergeProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) Debug.WriteLine("[FFmpeg Final Merge Output] " + ev.Data); };
                        mergeProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) errorOutput.AppendLine(ev.Data); };

                        mergeProcess.Start();
                        mergeProcess.BeginOutputReadLine();
                        mergeProcess.BeginErrorReadLine();
                        mergeProcess.WaitForExit(15000);

                        if (mergeProcess.ExitCode != 0)
                        {
                            System.Windows.MessageBox.Show($"Error combining video and audio: {errorOutput.ToString()}");
                            return;
                        }

                        if (!File.Exists(outputFilePath) || new FileInfo(outputFilePath).Length == 0)
                        {
                            System.Windows.MessageBox.Show("Final output file is invalid or empty.");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"Error merging audio segments: {ex.Message}");
                    }
                }
                else
                {
                    File.Copy(tempVideoFile, outputFilePath, true);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Error merging segments: {ex.Message}");
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

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            if (ffmpegProcess != null && !ffmpegProcess.HasExited)
            {
                try
                {
                    ffmpegProcess.StandardInput.WriteLine("q");
                    ffmpegProcess.WaitForExit(15000);
                    if (!ffmpegProcess.HasExited)
                    {
                        ffmpegProcess.Kill();
                    }
                    ffmpegProcess.Dispose();
                    ffmpegProcess = null;

                    audioCts?.Cancel();
                    await StopAudioRecordingAsync();

                    if (videoSegmentFiles.Count > 0)
                    {
                        string lastVideoSegment = videoSegmentFiles[videoSegmentFiles.Count - 1];
                        TimeSpan segmentDuration = DateTime.Now - startTime + pausedTime;
                        if (!File.Exists(lastVideoSegment) || new FileInfo(lastVideoSegment).Length == 0 || segmentDuration.TotalSeconds < 0.5)
                        {
                            Debug.WriteLine($"Invalid video segment file: {lastVideoSegment} (Duration: {segmentDuration.TotalSeconds}s, Size: {new FileInfo(lastVideoSegment).Length} bytes)");
                            videoSegmentFiles.RemoveAt(videoSegmentFiles.Count - 1);
                            if (audioSegmentFiles.Count > 0)
                            {
                                string lastAudioSegment = audioSegmentFiles[audioSegmentFiles.Count - 1];
                                if (!File.Exists(lastAudioSegment) || new FileInfo(lastAudioSegment).Length == 0)
                                {
                                    Debug.WriteLine($"Invalid audio segment file: {lastAudioSegment} (Size: {new FileInfo(lastAudioSegment).Length} bytes)");
                                    audioSegmentFiles.RemoveAt(audioSegmentFiles.Count - 1);
                                }
                            }
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
                string ffprobeArgs = $"-i \"{outputFilePath}\" -show_streams -print_format json";
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffprobe",
                        Arguments = ffprobeArgs,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                Debug.WriteLine($"Final output info: {output}");
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
            videoSegmentFiles.Clear();
            audioSegmentFiles.Clear();
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
        public string SystemAudioDevice { get; set; }
        public string MicAudioDevice { get; set; }
    }
}