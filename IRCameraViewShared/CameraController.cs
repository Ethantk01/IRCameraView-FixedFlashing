using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace IRCameraView
{
	public enum IRFrameFilter
	{
		None,
		Raw,
		Illuminated
	}

	public enum IRMappingMode
	{
		None,
		Green
	}

	public class CameraController
    {
		[System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E1B3D3A")]
		[System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
		interface IMemoryBufferByteAccess
		{
			void GetBuffer(out byte[] buffer, out uint capacity);
		}

		public MediaFrameReader? MediaFrameReader { get; private set; }
		public MediaPlayer MediaPlayer { get; private set; }
		public MediaCapture? MediaCapture { get;
			private set; }
		public List<MediaFrameSourceGroup> Devices { get; private set; }
		public MediaFrameSourceGroup SourceGroup { get; private set; }

		public IRFrameFilter FrameFilter { get; set; }
		public IRMappingMode MappingMode { get; set; }

        public SoftwareBitmap LatestBitmap { get
			{
				return _latestBitmap;
			}
			set
			{
				if(_latestBitmap!=null) _latestBitmap.Dispose();
				_latestBitmap = value;
			}
		}
        private SoftwareBitmap _latestBitmap;


        public VideoDeviceController? Controller { get {
				return MediaCapture?.VideoDeviceController; } }

		public delegate void FrameReady(SoftwareBitmap bitmap);
		public event FrameReady OnFrameReady;

		private SoftwareBitmap _backBuffer;
		//private bool _isInitialized = false;

		// Brightness-based frame classification. IsIlluminated (reported by the driver) isn't
		// reliable across hardware, so instead we measure each frame's actual brightness and
		// classify it as "bright" or "dark" relative to a self-calibrating midpoint. This can't
		// get stuck matching zero frames the way a driver flag can.
		private double _minBrightness = double.NaN;
		private double _maxBrightness = double.NaN;
		private bool _lastClassifiedBright = true;
		private readonly Stopwatch _sinceLastEmittedFrame = Stopwatch.StartNew();

		// If nothing has matched the filter in a while (e.g. brightness never actually varies
		// on this camera), show a frame anyway rather than staying black forever.
		private static readonly TimeSpan FilterFallbackTimeout = TimeSpan.FromMilliseconds(750);

		// 0 = needs a big brightness swing before switching bright/dark classification
		// (most stable, least likely to blackout, but slower to react).
		// 1 = switches on the smallest brightness difference (most reactive, but can
		// flicker between classifications if the signal is noisy).
		public double Sensitivity { get; set; } = 0.5;

		// If true, the most recent bright ("illuminated") frame and the most recent dark
		// ("raw") frame are merged into a single output frame instead of emitting whichever
		// frame just arrived on its own. This removes the flashing without dropping any
		// frames (unlike FrameFilter) and without throwing away real signal from either frame
		// (unlike rescaling a single frame's brightness), since every emitted frame carries
		// whatever each source frame saw at its brightest.
		public bool MergeFrames { get; set; } = false;

		// Pixel buffers (Bgra8) for the most recently seen bright/dark frames, used to build
		// the merged output. Kept alongside the dimensions they were captured at so a mid-
		// stream resolution change can't merge mismatched buffers.
		// Deliberately NOT keyed by the ClassifyIsBright() label - that classifier is tuned
		// with hysteresis for stable FrameFilter switching, not for reliably pairing up a
		// bright/dark frame every time. Trusting it here meant an occasional misclassification
		// would swap which stored buffer was "bright" vs "dark", and since the merge below is
		// asymmetric (dark = base), a swap flipped the whole image. Instead we just keep the
		// two most recently arrived frames and decide brighter/darker by directly comparing
		// their actual measured brightness at merge time, which can't be fooled by a stale label.
		private byte[] _mergeSlotOld;
		private byte[] _mergeSlotNew;
		private int _mergeWidth;
		private int _mergeHeight;

		// True once construction has finished enumerating cameras without error. CameraPage
		// checks this (and Devices.Count) instead of the constructor throwing, since a thrown
		// constructor exception during Page navigation is unhandled by WinUI/UWP and crashes
		// the whole app on launch - e.g. on any machine without an infrared camera.
		public bool InitializationFailed { get; private set; }
		public string? InitializationError { get; private set; }

		public CameraController()
		{
			FrameFilter = IRFrameFilter.None;
			MappingMode = IRMappingMode.None;
			MediaCapture = null;
			Devices = new List<MediaFrameSourceGroup>();

			try
			{
				LoadCameras(MediaFrameSourceKind.Infrared);
			}
			catch (Exception ex)
			{
				InitializationFailed = true;
				InitializationError = ex.Message;
				return;
			}

			if (Devices == null || Devices.Count == 0)
			{
				InitializationFailed = true;
				InitializationError = "No infrared cameras were found.";
			}
		}

        public List<MediaFrameSourceGroup> LoadCameras(MediaFrameSourceKind allowedKind)
		{
			return LoadCameras([allowedKind]);
		}

        public List<MediaFrameSourceGroup> LoadCameras(List<MediaFrameSourceKind>? allowedKinds = null)
		{
			Devices = new List<MediaFrameSourceGroup>();
			// ConfigureAwait(false) stops the continuation trying to marshal back onto the
			// calling (UI) thread's dispatcher queue. Without it, calling this synchronously
			// from the UI thread deadlocks: WinRT posts the completion to the UI thread, but
			// the UI thread is blocked here waiting on .Result, so it can never run.
			var frameSources = MediaFrameSourceGroup.FindAllAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

			// Filter out unwanted camera types
			foreach (var device in frameSources)
				foreach (var sourceInfo in device.SourceInfos)
					if (allowedKinds == null || allowedKinds.Contains(sourceInfo.SourceKind))
						Devices.Add(device);

			return Devices;
		}

		public void SelectDevice(MediaFrameSourceGroup sourceGroup, bool exclusive = true)
		{
			if (MediaFrameReader != null)
			{
				MediaFrameReader.FrameArrived -= FrameArrived;
				MediaFrameReader.StopAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
				MediaFrameReader.Dispose();
				MediaFrameReader = null;
			}


			if (MediaCapture != null)
			{
				MediaCapture.Dispose();
				MediaCapture = null;
			}


			SourceGroup = sourceGroup;

			var mediaCapture = new MediaCapture();

			var profiles = MediaCapture.FindAllVideoProfiles(sourceGroup.Id);

			foreach (var profile in profiles)
			{
				var infos = profile.FrameSourceInfos.FirstOrDefault();
				var recordMedia = profile.SupportedRecordMediaDescription.FirstOrDefault();
				var keys = infos.DeviceInformation.Properties.Keys;
				var values = infos.DeviceInformation.Properties.Values;

				for (int i = 0; i < keys.Count(); ++i)
				{
					var key = keys.ElementAt(i);
					var value = values.ElementAt(i);
				}
			}

			MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings
			{
				SourceGroup = SourceGroup = sourceGroup,
				SharingMode = exclusive ? MediaCaptureSharingMode.ExclusiveControl : MediaCaptureSharingMode.SharedReadOnly,
				StreamingCaptureMode = StreamingCaptureMode.Video,
				MemoryPreference = MediaCaptureMemoryPreference.Cpu,
			};

			mediaCapture.InitializeAsync(settings).AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

			var frameSources = mediaCapture.FrameSources;
			if (frameSources.Count == 0) return;
			var frameSource = frameSources.First().Value;

			var preferredFormat = frameSource.SupportedFormats.First();

			frameSource.SetFormatAsync(preferredFormat).AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

			MediaFrameReader = mediaCapture.CreateFrameReaderAsync(frameSource).AsTask().ConfigureAwait(false).GetAwaiter().GetResult();
			MediaFrameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

			MediaFrameReader.FrameArrived += FrameArrived;

			MediaFrameReader.StartAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult();

            MediaCapture = mediaCapture;
        }

		public void SelectDeviceByIndex(int index)
		{
			if (index < 0 || index >= Devices.Count)
				throw new ArgumentOutOfRangeException(nameof(index), "Invalid device index.");

			SelectDevice(Devices[index]);
		}

		public List<string> GetDeviceNames()
		{
			return Devices.Select(d => d.DisplayName).ToList();
		}

		public void CaptureImage()
		{
            CaptureImage(LatestBitmap);
        }

        public void CaptureImage(SoftwareBitmap bitmap)
        {
            SaveBitmap(SoftwareBitmap.Copy(bitmap));
        }

        private async Task SaveBitmap(SoftwareBitmap bitmap)
        {
			if (bitmap == null) return;
            try
            {

                StorageFile file = await KnownFolders.PicturesLibrary.CreateFileAsync("Infrared.jpg", CreationCollisionOption.GenerateUniqueName);

                using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
					
                    encoder.SetSoftwareBitmap(bitmap);

                    var propertySet = new BitmapPropertySet();
                    var qualityValue = new BitmapTypedValue(0.9, PropertyType.Single);
                    propertySet.Add("ImageQuality", qualityValue);

                    await encoder.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save image: {ex.Message}");
            }
        }

        public static SoftwareBitmap ConvertToGreenOnly(SoftwareBitmap inputBitmap)
		{
            if (inputBitmap == null)
                throw new ArgumentNullException(nameof(inputBitmap));

            var bitmap = inputBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8
                ? SoftwareBitmap.Convert(inputBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                : SoftwareBitmap.Copy(inputBitmap);

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;

            byte[] pixels = new byte[width * height * 4];
            bitmap.CopyToBuffer(pixels.AsBuffer());

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte g = pixels[i + 1];
                byte a = pixels[i + 3];

                pixels[i + 0] = 0;
                pixels[i + 1] = g;
                pixels[i + 2] = 0;
                pixels[i + 3] = a;
            }

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(pixels.AsBuffer());

            return result;
        }

        // Samples a subset of pixels (not every pixel, for speed) and returns the average
        // 0-255 brightness across the R/G/B channels. Assumes Bgra8 input, which FrameArrived
        // guarantees before this is ever called.
        private static double ComputeAverageBrightness(SoftwareBitmap bitmap)
        {
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            if (width == 0 || height == 0) return 0;

            byte[] pixels = new byte[width * height * 4];
            bitmap.CopyToBuffer(pixels.AsBuffer());

            const int targetSamples = 4000;
            int totalPixels = width * height;
            int stride = Math.Max(1, totalPixels / targetSamples);

            long sum = 0;
            int count = 0;
            for (int i = 0; i < totalPixels; i += stride)
            {
                int offset = i * 4; // BGRA8
                sum += pixels[offset] + pixels[offset + 1] + pixels[offset + 2];
                count++;
            }

            return count == 0 ? 0 : sum / (double)(count * 3);
        }

        // Classifies a frame as "bright" or "dark" relative to a self-calibrating midpoint
        // between the recent brightness envelope's min and max, with a hysteresis band (sized
        // by Sensitivity) so borderline frames keep the previous classification instead of
        // flickering.
        private bool ClassifyIsBright(SoftwareBitmap bitmap)
        {
            double brightness = ComputeAverageBrightness(bitmap);

            if (double.IsNaN(_minBrightness))
            {
                _minBrightness = _maxBrightness = brightness;
            }

            // Fast attack toward new extremes, slow decay back down, so the envelope tracks
            // the actual bright/dark swing without permanently locking onto one outlier frame.
            const double attack = 0.3;
            const double decay = 0.02;

            _maxBrightness += (brightness - _maxBrightness) * (brightness > _maxBrightness ? attack : decay);
            _minBrightness += (brightness - _minBrightness) * (brightness < _minBrightness ? attack : decay);

            double mid = (_maxBrightness + _minBrightness) / 2.0;
            double range = Math.Max(_maxBrightness - _minBrightness, 1.0);
            double hysteresis = range * (1.0 - Math.Clamp(Sensitivity, 0.0, 1.0)) * 0.5;

            if (brightness > mid + hysteresis) _lastClassifiedBright = true;
            else if (brightness < mid - hysteresis) _lastClassifiedBright = false;
            // else: inside the hysteresis band, keep the previous classification.

            return _lastClassifiedBright;
        }

        // Copies the given frame's pixels into the rolling two-frame buffer (oldest dropped),
        // regardless of any bright/dark classification. If the frame size doesn't match what's
        // already stored, both slots are cleared so we never merge mismatched buffers.
        private void StoreFrameForMerge(SoftwareBitmap bitmap)
        {
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            if (width == 0 || height == 0) return;

            if (width != _mergeWidth || height != _mergeHeight)
            {
                _mergeWidth = width;
                _mergeHeight = height;
                _mergeSlotOld = null;
                _mergeSlotNew = null;
            }

            byte[] pixels = new byte[width * height * 4];
            bitmap.CopyToBuffer(pixels.AsBuffer());

            _mergeSlotOld = _mergeSlotNew;
            _mergeSlotNew = pixels;
        }

        // Average 0-255 luma across an already-copied Bgra8 pixel buffer. Used to decide,
        // at merge time, which of the two stored frames is actually the brighter one - see
        // BuildMergedFrame for why this is done here instead of trusting a stored label.
        private static double AverageBrightness(byte[] pixels)
        {
            long sum = 0;
            int count = pixels.Length / 4;
            for (int i = 0; i < pixels.Length; i += 4)
                sum += pixels[i + 0] + pixels[i + 1] + pixels[i + 2];
            return count == 0 ? 0 : sum / (double)(count * 3);
        }

        // How dark a dark-frame pixel needs to be (0-255 luma) before the bright frame is
        // allowed to lift it. Pixels at or above this are left as the dark frame recorded
        // them; only pixels below it get pulled toward the bright frame's value.
        private const double MergeShadowThreshold = 96.0;

        // Combines the two most recently arrived frames into one bitmap using the darker one
        // as the base and lifting only its genuinely dark areas toward the brighter frame's
        // value. Which stored frame is "darker" is decided here, by directly comparing their
        // measured brightness, rather than trusting whatever bright/dark label the frame
        // arrived with - see the field comments above for why. A straight average was tried
        // first, but wherever one frame reads near-black and the other near-white for the same
        // pixel (e.g. the illuminator hitting the subject), the average lands at flat mid-gray
        // for both, erasing contrast instead of preserving detail from either frame. Basing on
        // the darker frame and only lifting shadows keeps the subject at its natural exposure
        // (fixing the "too bright" problem) while still recovering the shadow/background detail
        // only the brighter frame captured (fixing the flashing/black-frame problem this
        // feature exists for). Returns null until two frames have been seen.
        private SoftwareBitmap BuildMergedFrame()
        {
            var slotOld = _mergeSlotOld;
            var slotNew = _mergeSlotNew;
            if (slotOld == null || slotNew == null) return null;
            if (slotOld.Length != slotNew.Length) return null;

            bool oldIsDarker = AverageBrightness(slotOld) <= AverageBrightness(slotNew);
            var dark = oldIsDarker ? slotOld : slotNew;
            var bright = oldIsDarker ? slotNew : slotOld;

            byte[] merged = new byte[bright.Length];
            for (int i = 0; i < merged.Length; i += 4)
            {
                double darkLuma = (dark[i + 0] + dark[i + 1] + dark[i + 2]) / 3.0;

                // 0 once darkLuma reaches the threshold (no change), ramping up to 1 as
                // darkLuma approaches 0 (fully replaced by the bright frame's value). Squaring
                // eases the ramp in, so midtones near the threshold barely move and only true
                // shadows get pulled hard - this is what keeps already-fine detail from
                // washing out toward gray.
                double t = Math.Clamp(1.0 - darkLuma / MergeShadowThreshold, 0.0, 1.0);
                double weight = t * t;

                merged[i + 0] = (byte)Math.Clamp(dark[i + 0] + weight * (bright[i + 0] - dark[i + 0]), 0, 255);
                merged[i + 1] = (byte)Math.Clamp(dark[i + 1] + weight * (bright[i + 1] - dark[i + 1]), 0, 255);
                merged[i + 2] = (byte)Math.Clamp(dark[i + 2] + weight * (bright[i + 2] - dark[i + 2]), 0, 255);
                merged[i + 3] = bright[i + 3]; // Alpha taken from either; they should match.
            }

            var result = new SoftwareBitmap(BitmapPixelFormat.Bgra8, _mergeWidth, _mergeHeight, BitmapAlphaMode.Premultiplied);
            result.CopyFromBuffer(merged.AsBuffer());
            return result;
        }

		private void FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
		{
			using (var frameReference = sender.TryAcquireLatestFrame())
			{
				var videoMediaFrame = frameReference?.VideoMediaFrame;

				var softwareBitmap = videoMediaFrame?.SoftwareBitmap;

				if (softwareBitmap == null) return;
				if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || softwareBitmap.BitmapAlphaMode != BitmapAlphaMode.Premultiplied)
					softwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

				softwareBitmap = Interlocked.Exchange(ref _backBuffer, softwareBitmap);

				SoftwareBitmap hi;
				while ((hi = Interlocked.Exchange(ref _backBuffer, null)) != null)
				{
                    if (MappingMode == IRMappingMode.Green)
                    {
                        var greenBitmap = ConvertToGreenOnly(hi);
                        hi.Dispose();
                        hi = greenBitmap;
                    }

					// Only FrameFilter needs the bright/dark classification. MergeFrames decides
					// brighter/darker for itself at merge time (see BuildMergedFrame).
					bool isBright = FrameFilter != IRFrameFilter.None && ClassifyIsBright(hi);

					bool matchesFilter = FrameFilter == IRFrameFilter.None
						|| (FrameFilter == IRFrameFilter.Illuminated && isBright)
						|| (FrameFilter == IRFrameFilter.Raw && !isBright);

					var timedOut = _sinceLastEmittedFrame.Elapsed > FilterFallbackTimeout;

					if (OnFrameReady != null && (matchesFilter || timedOut))
					{
						_sinceLastEmittedFrame.Restart();

						if (MergeFrames)
						{
							StoreFrameForMerge(hi);
							var merged = BuildMergedFrame();
							OnFrameReady(LatestBitmap = merged ?? SoftwareBitmap.Copy(hi));
						}
						else
						{
							OnFrameReady(LatestBitmap = SoftwareBitmap.Copy(hi));
						}
					}

					hi.Dispose();
				}

                softwareBitmap?.Dispose();
            }
		}
	}
}
