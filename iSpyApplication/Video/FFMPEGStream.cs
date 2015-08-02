using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using AForge.Video;
using iSpy.Video.FFMPEG;
using iSpyApplication.Audio;
using iSpyApplication.Audio.streams;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace iSpyApplication.Video
{
    public class FFMPEGStream : IVideoSource, IAudioSource, ISupportsAudio
    {
        private int _framesReceived;
        private ManualResetEvent _stopEvent;
        private Thread _thread;
        private string _source;
        private int _initialSeek = -1;

        #region Audio
        private float _gain;
        private bool _listening;
        private volatile bool _stopping;

        private BufferedWaveProvider _waveProvider;
        public SampleChannel _sampleChannel;

        public BufferedWaveProvider WaveOutProvider { get; set; }
        public VolumeWaveProvider16New VolumeProvider { get; set; }
        #endregion

        private Int64 _lastFrame = DateTime.MinValue.Ticks;

        public DateTime LastFrame
        {
            get { return new DateTime(_lastFrame); }
            set { Interlocked.Exchange(ref _lastFrame, value.Ticks); }
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="FFMPEGStream"/> class.
        /// </summary>
        /// 
        public FFMPEGStream()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="FFMPEGStream"/> class.
        /// </summary>
        /// 
        /// <param name="source">URL, which provides video stream.</param>
        public FFMPEGStream(string source)
        {
            _source = source;
        }

        public IAudioSource OutAudio;

        #region IVideoSource Members

        /// <summary>
        /// New frame event.
        /// </summary>
        /// 
        /// <remarks><para>Notifies clients about new available frame from video source.</para>
        /// 
        /// <para><note>Since video source may have multiple clients, each client is responsible for
        /// making a copy (cloning) of the passed video frame, because the video source disposes its
        /// own original copy after notifying of clients.</note></para>
        /// </remarks>
        /// 
        public event NewFrameEventHandler NewFrame;
        public event VideoSourceErrorEventHandler VideoSourceError;
        public event PlayingFinishedEventHandler PlayingFinished;

        public event DataAvailableEventHandler DataAvailable;
        public event LevelChangedEventHandler LevelChanged;
        public event AudioFinishedEventHandler AudioFinished;

        public event HasAudioStreamEventHandler HasAudioStream;

        /// <summary>
        /// Video source.
        /// </summary>
        /// 
        /// <remarks>URL, which provides video stream.</remarks>
        /// 
        public string Source
        {
            get { return _source; }
            set { _source = value; }
        }

        /// <summary>
        /// Received bytes count.
        /// </summary>
        /// 
        /// <remarks>Number of bytes the video source provided from the moment of the last
        /// access to the property.
        /// </remarks>
        /// 
        public long BytesReceived
        {
            get
            {
                //long bytes = 0;
                //bytesReceived = 0;
                return 0;
            }
        }

        /// <summary>
        /// Received frames count.
        /// </summary>
        /// 
        /// <remarks>Number of frames the video source provided from the moment of the last
        /// access to the property.
        /// </remarks>
        /// 
        public int FramesReceived
        {
            get
            {
                int frames = _framesReceived;
                _framesReceived = 0;
                return frames;
            }
        }

        /// <summary>
        /// State of the video source.
        /// </summary>
        /// 
        /// <remarks>Current state of video source object - running or not.</remarks>
        /// 
        public bool IsRunning
        {
            get
            {
                return _thread != null && !_thread.Join(TimeSpan.Zero);
            }
        }

        private Thread _eventing;

        /// <summary>
        /// Start video source.
        /// </summary>
        /// 
        /// <remarks>Starts video source and return execution to caller. Video source
        /// object creates background thread and notifies about new frames with the
        /// help of <see cref="NewFrame"/> event.</remarks>
        /// 
        /// <exception cref="ArgumentException">Video source is not specified.</exception>
        /// 
        public void Start()
        {
            if (IsRunning) return;

            if (string.IsNullOrEmpty(_source))
                throw new ArgumentException("Video source is not specified.");

            //Debug.WriteLine("Starting "+_source);
            _framesReceived = 0;

            // create events
            if (_stopEvent != null)
            {
                _stopEvent.Close();
                _stopEvent.Dispose();
                _stopEvent = null;
            }
            _stopEvent = new ManualResetEvent(false);

            // create and start new thread

            _thread = new Thread(FfmpegListener) { Name = "ffmpeg " + _source, IsBackground = true };
            _thread.Start();

            Seekable = IsFileSource;
            _initialSeek = -1;
        }

        private bool _paused;

        public bool IsPaused
        {
            get { return _paused; }
        }

        public void Play()
        {
            _paused = false;
            _sw.Start();
        }

        public void Pause()
        {
            _paused = true;
            _sw.Stop();
        }

        public double PlaybackRate = 1;

        private VideoFileReader _vfr;
        private bool IsFileSource
        {
            get { return _source != null && _source.IndexOf("://", StringComparison.Ordinal) == -1; }
        }

        private readonly Stopwatch _sw = new Stopwatch();

        public string Cookies = "";
        public string UserAgent = "";
        public string Headers = "";
        public int RTSPMode = 0;

        public int AnalyzeDuration = 2000;
        public int Timeout = 8000;

        ReasonToFinishPlaying _reasonToStop = ReasonToFinishPlaying.StoppedByUser;

        private void FfmpegListener()
        {
            _reasonToStop = ReasonToFinishPlaying.StoppedByUser;
            _vfr = null;
            bool open = false;
            string errmsg = "";
            _eventing = null;
            _stopping = false;
            try
            {
                Program.FFMPEGMutex.WaitOne();
                _vfr = new VideoFileReader();

                //ensure http/https is lower case for string compare in ffmpeg library
                int i = _source.IndexOf("://", StringComparison.Ordinal);
                if (i > -1)
                {
                    _source = _source.Substring(0, i).ToLower() + _source.Substring(i);
                }
                _vfr.Timeout = Timeout;
                _vfr.AnalyzeDuration = AnalyzeDuration;
                _vfr.Cookies = Cookies;
                _vfr.UserAgent = UserAgent;
                _vfr.Headers = Headers;
                _vfr.Flags = -1;
                _vfr.NoBuffer = true;
                _vfr.RTSPMode = RTSPMode;
                _vfr.Open(_source);
                open = true;
            }
            catch (Exception ex)
            {
                MainForm.LogExceptionToFile(ex, "FFMPEG");
            }
            finally
            {
                try
                {
                    Program.FFMPEGMutex.ReleaseMutex();
                }
                catch (ObjectDisposedException)
                {
                    //can happen on shutdown
                }
            }

            if (_vfr == null || !_vfr.IsOpen || !open)
            {
                ShutDown("Could not open stream" + ": " + _source);
                return;
            }
            if (_stopEvent.WaitOne(0))
            {
                ShutDown("");
                return;
            }

            bool hasaudio = false;


            if (_vfr.Channels > 0)
            {
                hasaudio = true;
                RecordingFormat = new WaveFormat(_vfr.SampleRate, 16, _vfr.Channels);
                _waveProvider = new BufferedWaveProvider(RecordingFormat);
                _sampleChannel = new SampleChannel(_waveProvider);
                _sampleChannel.PreVolumeMeter += SampleChannelPreVolumeMeter;
            }


            Duration = _vfr.Duration;

            _videoQueue = new ConcurrentQueue<Bitmap>();
            _audioQueue = new ConcurrentQueue<byte[]>();
            _eventing = new Thread(EventManager) { Name = "ffmpeg eventing", IsBackground = true };
            _eventing.Start();


            if (_initialSeek > -1)
                _vfr.Seek(_initialSeek);
            try
            {
                while (!_stopEvent.WaitOne(5) && !MainForm.ShuttingDown)
                {
                    var nf = NewFrame;
                    if (nf == null)
                        break;
                    if (!_paused)
                    {
                        object frame = _vfr.ReadFrame();
                        switch (_vfr.LastFrameType)
                        {
                            case 0:
                                //null packet
                                if ((DateTime.UtcNow - LastFrame).TotalMilliseconds > Timeout)
                                    throw new TimeoutException("Timeout reading from video stream");
                                break;
                            case 1:
                                LastFrame = DateTime.UtcNow;
                                if (hasaudio)
                                {
                                    var data = frame as byte[];
                                    if (data != null)
                                    {
                                        if (data.Length > 0)
                                        {
                                            ProcessAudio(data);
                                        }
                                    }
                                }
                                break;
                            case 2:
                                LastFrame = DateTime.UtcNow;

                                if (frame != null)
                                {
                                    var bmp = frame as Bitmap;
                                    if (bmp != null)
                                    {
                                        if (_videoQueue.Count < 20)
                                            _videoQueue.Enqueue(bmp);
                                    }
                                }
                                break;
                        }
                    }
                }

            }
            catch (Exception e)
            {
                MainForm.LogExceptionToFile(e, "FFMPEG");
                errmsg = e.Message;
            }

            _eventing.Join();

            if (_sampleChannel != null)
            {
                _sampleChannel.PreVolumeMeter -= SampleChannelPreVolumeMeter;
                _sampleChannel = null;
            }

            if (_waveProvider != null)
            {
                if (_waveProvider.BufferedBytes > 0)
                    _waveProvider.ClearBuffer();
            }

            ShutDown(errmsg);
        }

        private void ShutDown(string errmsg)
        {

            bool err = !String.IsNullOrEmpty(errmsg);
            if (err)
            {
                _reasonToStop = ReasonToFinishPlaying.DeviceLost;
            }

            if (IsFileSource && !err)
                _reasonToStop = ReasonToFinishPlaying.StoppedByUser;

            if (_vfr != null && _vfr.IsOpen)
            {
                try
                {
                    _vfr.Dispose(); //calls close
                }
                catch (Exception ex)
                {
                    MainForm.LogExceptionToFile(ex, "FFMPEG");
                }
            }

            if (PlayingFinished != null)
                PlayingFinished(this, _reasonToStop);
            if (AudioFinished != null)
                AudioFinished(this, _reasonToStop);


            _stopEvent.Close();
            _stopEvent.Dispose();
            _stopEvent = null;
            _stopping = false;
        }

        void ProcessAudio(byte[] data)
        {
            if (HasAudioStream != null)
            {
                HasAudioStream.Invoke(this, EventArgs.Empty);
                HasAudioStream = null;
            }
            try
            {
                var da = DataAvailable;
                if (da != null)
                {
                    _audioQueue.Enqueue(data);
                }
            }
            catch (NullReferenceException)
            {
                //DataAvailable can be removed at any time
            }
            catch (Exception ex)
            {
                MainForm.LogExceptionToFile(ex, "FFMPEG");
            }
        }

        void SampleChannelPreVolumeMeter(object sender, StreamVolumeEventArgs e)
        {
            var lc = LevelChanged;
            if (lc != null)
            {
                lc.Invoke(this, new LevelChangedEventArgs(e.MaxSampleValues));
            }

        }

        public bool Seekable;

        public long Time
        {
            get
            {
                if (_vfr.IsOpen)
                    return _vfr.VideoTime;
                return 0;
            }
        }
        public long Duration;


        public void Seek(float percentage)
        {
            int t = Convert.ToInt32((Duration / 1000d) * percentage);
            if (Seekable)
            {
                _sw.Stop();
                _initialSeek = t;
                _vfr.Seek(t);
                _sw.Reset();
                _sw.Start();
            }
        }

        #region Audio Stuff




        public float Gain
        {
            get { return _gain; }
            set
            {
                _gain = value;
                if (_sampleChannel != null)
                {
                    _sampleChannel.Volume = value;
                }
            }
        }

        public bool Listening
        {
            get
            {
                if (IsRunning && _listening)
                    return true;
                return false;

            }
            set
            {
                if (RecordingFormat == null)
                {
                    _listening = false;
                    return;
                }

                if (WaveOutProvider != null)
                {
                    if (WaveOutProvider.BufferedBytes > 0) WaveOutProvider.ClearBuffer();
                    WaveOutProvider = null;
                }


                if (value)
                {
                    WaveOutProvider = new BufferedWaveProvider(RecordingFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(500) };
                }

                _listening = value;
            }
        }

        public WaveFormat RecordingFormat { get; set; }

        #endregion

        /// <summary>
        /// Calls Stop
        /// </summary>
        public void SignalToStop()
        {
            Stop();
        }

        /// <summary>
        /// Calls Stop
        /// </summary>
        public void WaitForStop()
        {
            Stop();
        }

        /// <summary>
        /// Stop video source.
        /// </summary>
        /// 
        public void Stop()
        {
            if (IsRunning && !_stopping)
            {
                // wait for thread stop
                _stopping = true;
                _stopEvent.Set();

            }
        }
        #endregion


        private ConcurrentQueue<Bitmap> _videoQueue;
        private ConcurrentQueue<byte[]> _audioQueue;

        private void EventManager()
        {
            byte[] audio;
            Bitmap frame;

            while (!_stopEvent.WaitOne(5, false) && !App.ShuttingDown)
            {
                var da = DataAvailable;
                var nf = NewFrame;

                if (_videoQueue.TryDequeue(out frame))
                {
                    //new frame
                    if (nf != null)
                        nf.Invoke(this, new NewFrameEventArgs(frame));

                    frame.Dispose();
                    frame = null;
                }


                if (_audioQueue.TryDequeue(out audio))
                {
                    if (da != null)
                        da.Invoke(this, new DataAvailableEventArgs(audio));

                    var sampleBuffer = new float[audio.Length];
                    _sampleChannel.Read(sampleBuffer, 0, audio.Length);

                    _waveProvider.AddSamples(audio, 0, audio.Length);

                    if (WaveOutProvider != null && Listening)
                    {
                        WaveOutProvider.AddSamples(audio, 0, audio.Length);
                    }
                }
            }
            while (_videoQueue.TryDequeue(out frame))
            {
                frame.Dispose();
                frame = null;
            }
        }

    }
}