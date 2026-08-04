using System;
using System.Linq;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Media.Capture;
using Windows.Media.Devices;
using Windows.Media.Capture.Frames;
using Windows.Graphics.Imaging;
using System.Threading.Tasks;
using System.Collections.ObjectModel;


#if NETFX_CORE
// UWP code
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
#else
// WinUI 3 code
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
#endif

namespace IRCameraView
{
    public sealed partial class CameraPage : Page
    {
        private CameraController camera;
        static StorageFile videoFile;

        public ObservableCollection<string> CameraNames { get; } = new ObservableCollection<string>();

        public CameraPage()
        {
            InitializeComponent();
            camera = new CameraController();
            StartCapture();
            ReloadDevices();
#if NETFX_CORE
#else
            PreviewModeBox.SelectedIndex = 1;
#endif
        }

        public Grid GetTitleBar()
        {
            return TitleBarGripper;
        }

        void ReloadDevices()
        {
            DeviceComboBox.Items.Clear();

            var names = camera?.GetDeviceNames();
            if (names == null) return;


            foreach (var device in names)
                DeviceComboBox.Items.Add(device);

            if (DeviceComboBox.Items.Count > 0)
                DeviceComboBox.SelectedIndex = 0;

            if (camera?.Controller == null) return;

            try
            {
                InfraredTorchControl torchControl = camera.Controller.InfraredTorchControl;

                if (torchControl.IsSupported)
                {
                    TorchControlGrid.Visibility = Visibility.Visible;
                    IRTorchBox.ItemsSource = torchControl.SupportedModes;
                }

                var photoControl = camera.Controller.AdvancedPhotoControl;
                if (photoControl.Supported)
                {
                        PhotoModeBox.Items.Clear();
                        foreach (var model in photoControl.SupportedModes)
                    PhotoModeBox.Items.Add(model);
                    PhotoModeGrid.Visibility = Visibility.Visible;
                }
            }
            catch { }

        }

        void EnableMargin()
        {
            var margin = new Thickness(20);
            margin.Top += 32;
            margin.Right *= 2;
            margin.Right += PhotoButton.Width;
            ImageGrid.Margin = margin;

            ImageViewbox.Stretch = Stretch.Uniform;
#if NETFX_CORE
ImageBorder.CornerRadius = new CornerRadius(0);
#else
            ImageBorder.CornerRadius = new CornerRadius(6);
#endif

        }

        void DisableMargin()
        {
            ImageGrid.Margin = new Thickness(0);
            ImageBorder.CornerRadius = new CornerRadius(0);

            ImageViewbox.Stretch = Stretch.UniformToFill;
        }

        private void StartCapture()
        {
            ImageElement.Source = new SoftwareBitmapSource();

            camera = new CameraController();
            camera.OnFrameReady += OnFrameArrived;
        }

        async Task InvokeOnMainThread(Action callback)
        {
#if NETFX_CORE
            await ImageElement.Dispatcher?.RunAsync(CoreDispatcherPriority.Normal ,() => callback());
#else
            ImageElement?.DispatcherQueue?.TryEnqueue(() => callback());
#endif
        }

        private void OnFrameArrived(SoftwareBitmap bitmap)
        {
            InvokeOnMainThread(async () =>
            {
                try
                {
                    var imageSource = (SoftwareBitmapSource)ImageElement.Source;
                    await imageSource.SetBitmapAsync(bitmap);
                }
                catch { }
            }).Wait();
        }

        private void FrameFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox comboBox && camera != null)
                camera.FrameFilter = (IRFrameFilter)comboBox.SelectedIndex;
        }

        private void SensitivitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (camera != null) camera.Sensitivity = e.NewValue;
        }

        private void MergeFramesCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (camera != null) camera.MergeFrames = MergeFramesCheckBox.IsChecked ?? false;
        }

        private void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (camera == null) return;
            if (DeviceComboBox.SelectedIndex >= 0)
                camera.SelectDeviceByIndex(DeviceComboBox.SelectedIndex);
        }

        private async void TakePhoto_Click(object sender, RoutedEventArgs e)
        {
            camera.CaptureImage();
            FlashScreenAsync();
        }


        private async void TakeVideo_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is ToggleButton toggleButton)
                {
                    if (toggleButton.IsChecked ?? true)
                    {
                        videoFile = await KnownFolders.VideosLibrary.CreateFileAsync("IRRecording.mp4", CreationCollisionOption.GenerateUniqueName);
                        var encodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Auto);

                        var allVideoProfiles = camera?.Controller?.GetAvailableMediaStreamProperties(MediaStreamType.VideoRecord)?.OfType<VideoEncodingProperties>().ToList();
                        if (allVideoProfiles == null) return;

                        var first = allVideoProfiles[0];

                        encodingProfile.Video.Width = first.Width;
                        encodingProfile.Video.Height = first.Height;

                        encodingProfile.Video.FrameRate.Numerator = first.FrameRate.Numerator;
                        encodingProfile.Video.FrameRate.Denominator = first.FrameRate.Denominator;

                        encodingProfile.Audio = null;

                        await camera?.MediaCapture?.StartRecordToStorageFileAsync(encodingProfile, videoFile);
                    }
                    else
                    {
                        await camera.MediaCapture.StopRecordAsync();

                        var successDialog = new ContentDialog()
                        {
                            Title = "Recording Saved",
                            Content = "Video saved to: " + videoFile.Path,
                            CloseButtonText = "OK",
                            XamlRoot = Content.XamlRoot
                        };
                        await successDialog.ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                var successDialog = new ContentDialog()
                {
                    Title = "Failed to record",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                };
                await successDialog.ShowAsync();
            }
        }

        private async Task FlashScreenAsync()
        {
            FlashOverlay.Opacity = 1;

            await Task.Delay(100);

            var fadeDuration = TimeSpan.FromMilliseconds(200);
            var animation = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = new Duration(fadeDuration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            Storyboard.SetTarget(animation, FlashOverlay);
            Storyboard.SetTargetProperty(animation, "Opacity");

            storyboard.Begin();

            await Task.Delay(fadeDuration);
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox && camera != null)
                camera.MappingMode = (IRMappingMode)(sender as ComboBox).SelectedIndex;
        }

        private void PreviewModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PreviewModeBox.SelectedIndex == 1) EnableMargin();
            if (PreviewModeBox.SelectedIndex == 0) DisableMargin();
        }

        private void PhotoModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            camera.Controller.AdvancedPhotoControl.Configure(new AdvancedPhotoCaptureSettings
            {
                Mode = (PhotoModeBox.SelectedItem as AdvancedPhotoMode?) ?? AdvancedPhotoMode.Standard
            });
        }

        private void IRTorchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedMode = (IRTorchBox.SelectedItem as InfraredTorchMode?) ?? InfraredTorchMode.AlternatingFrameIllumination;
            camera.Controller.InfraredTorchControl.CurrentMode = selectedMode;
        }

        MediaFrameSourceKind GetSelectedCameraKind()
        {
            switch (CameraType.SelectedIndex)
            {
                default:
                case 0: return MediaFrameSourceKind.Infrared;
                case 1: return MediaFrameSourceKind.Color;
                case 2: return MediaFrameSourceKind.Depth;
            }
        }

        private void CameraType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            camera?.LoadCameras(GetSelectedCameraKind());
            ReloadDevices();
        }
    }
}
