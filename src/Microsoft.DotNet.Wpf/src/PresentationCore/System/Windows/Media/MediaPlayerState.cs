// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

//
//

using System;
using System.Threading;
using System.Security;
using System.Security.Permissions;
using System.Diagnostics;
using System.ComponentModel;
using MS.Internal;
using MS.Internal.PresentationCore;                        // SecurityHelper
using MS.Win32;
using System.IO.Packaging;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Composition;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Navigation;
using System.Runtime.InteropServices;
using System.IO;
using System.Security.AccessControl;//for semaphore access permissions
using System.Net;
using Microsoft.Win32;
using SR=MS.Internal.PresentationCore.SR;
using SRID=MS.Internal.PresentationCore.SRID;
using UnsafeNativeMethods=MS.Win32.PresentationCore.UnsafeNativeMethods;

//
// Disable the warnings that C# emmits when it finds pragmas it does not recognize, this is to
// get rid of false positive PreSharp warning
//
#pragma warning disable 1634, 1691


namespace System.Windows.Media
{
    #region MediaPlayerState

    /// <summary>
    /// MediaPlayerState
    /// Holds all of the local state that is required for playing media. This is
    /// separated out into a separate class because MediaPlayer needs to be
    /// Animatable, but then that means it needs to be Freezable. However, media
    /// state cannot really be frozan (media piplines progress according to time
    /// and are very expensive), so instead, we make the "Frozen" object copy the
    /// state object around. Doing this will also help in the remote case where
    /// we need to handle MediaPlayer quite differently on the channel in the
    /// remote and local cases.
    /// </summary>
    internal class MediaPlayerState
    {
        #region Constructors and Finalizers

        /// <summary>
        /// Constructor
        /// </summary>
        internal
        MediaPlayerState(
            MediaPlayer     mediaPlayer
            )
        {
            _dispatcher = mediaPlayer.Dispatcher;

            Init();

            CreateMedia(mediaPlayer);

            //
            // We need to know about new frames when they are sent so that we can
            // capture the image data in the synchronous case.
            //
            _mediaEventsHelper.NewFrame += new EventHandler(OnNewFrame);

            //
            // Opened is actually fired when the media is prerolled.
            //
            _mediaEventsHelper.MediaPrerolled += new EventHandler(OnMediaOpened);
        }

        // Need to add support for base uri.

        /// <summary>
        /// Initialize local variables to their default state. After a close we want to restore this too, same as
        /// after construction.
        /// </summary>
        private
        void
        Init()
        {
            _volume = DEFAULT_VOLUME;
            _balance = DEFAULT_BALANCE;
            _speedRatio = 1.0;
            _paused = false;
            _muted = false;
            _sourceUri = null;
            _scrubbingEnabled = false;
        }

        /// <summary>
        /// Finalizer to remove the event handler from AppDomain.ProcessExit
        /// </summary>
        /// <SecurityNote>
        ///     Critical: Helper.ProcessExitHandler is SecurityCritical as it accesses native
        ///               resources
        ///     TreatAsSafe: This doesn't actually call ProcessExitHandler, just detaches it
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        ~MediaPlayerState()
        {
            if (_helper != null)
            {
                AppDomain.CurrentDomain.ProcessExit -= _helper.ProcessExitHandler;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Internal IsBuffering
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This calls into unmanaged code and also acceses _nativemedia
        ///     TreatAsSafe: This information about whether buffering is on is safe to expose
        /// </SecurityNote>
        internal bool IsBuffering
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();
                bool isBuffering = false;
                HRESULT.Check(MILMedia.IsBuffering(_nativeMedia, ref isBuffering));
                return isBuffering;
            }
        }

        /// <summary>
        /// Internal CanPause
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is critical because it acceses _nativemedia and calls into unmanaged code
        ///     TreatAsSafe: This is safe to expose since giving out information about
        ///     whether media can be paused is safe
        /// </SecurityNote>
        internal bool CanPause
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();
                bool canPause = false;
                HRESULT.Check(MILMedia.CanPause(_nativeMedia, ref canPause));
                return canPause;
            }
        }

        /// <summary>
        /// Internal DownloadProgress
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is critical it calls into unmanged code and accesses _nativeMedia
        ///     TreatAsSafe: This is safe because the critical resource is not exposed and
        ///     returning download progress is safe
        /// </SecurityNote>
        internal double DownloadProgress
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();
                double downloadProgress = 0;
                HRESULT.Check(MILMedia.GetDownloadProgress(_nativeMedia, ref downloadProgress));
                return downloadProgress;
            }
        }

        /// <summary>
        /// Internal BufferingProgress
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is critical it calls into unmanged code and accesses _nativeMedia
        ///     TreatAsSafe: This is safe because the critical resource is not exposed and
        ///     returning buffering progress is safe
        /// </SecurityNote>
        internal double BufferingProgress
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();
                double bufferingProgress = 0;
                HRESULT.Check(MILMedia.GetBufferingProgress(_nativeMedia, ref bufferingProgress));
                return bufferingProgress;
            }
        }

        /// <summary>
        /// Returns the Height
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is critical it calls into unmanged code and accesses _nativeMedia
        ///     TreatAsSafe: This is safe because the critical resource is not exposed and
        ///     returning natural video height is safe
        /// </SecurityNote>
        internal Int32 NaturalVideoHeight
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();

                UInt32 height = 0;

                HRESULT.Check(MILMedia.GetNaturalHeight(_nativeMedia, ref height));
                return (Int32)height;
            }
        }

        /// <summary>
        /// Returns the Width
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is critical it calls into unmanged code and accesses _nativeMedia
        ///     TreatAsSafe: This is safe because the critical resource is not exposed and
        ///     returning natural video width is safe
        /// </SecurityNote>
        internal Int32 NaturalVideoWidth
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();

                UInt32 width = 0;

                HRESULT.Check(MILMedia.GetNaturalWidth(_nativeMedia, ref width));
                return (Int32)width;
            }
        }

        /// <summary>
        /// If media has audio content
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is critical it calls into unmanged code and accesses _nativeMedia
        ///     TreatAsSafe: This is safe because the critical resource is not exposed and
        ///     returning whether media has audio is safe
        /// </SecurityNote>
        internal bool HasAudio
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();

                bool hasAudio = true;

                HRESULT.Check(MILMedia.HasAudio(_nativeMedia, ref hasAudio));
                return hasAudio;
            }
        }

        /// <summary>
        /// If the media has video content
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is critical it calls into unmanged code and accesses _nativeMedia
        ///     TreatAsSafe: This is safe because the critical resource is not exposed and
        ///     returning whether media has video is safe
        /// </SecurityNote>
        internal bool HasVideo
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();

                bool hasVideo = false;

                HRESULT.Check(MILMedia.HasVideo(_nativeMedia, ref hasVideo));
                return hasVideo;
            }
        }

        /// <summary>
        /// Location of the media to play. Open opens the media, this property
        /// allows the source that is currently playing to be retrieved.
        /// </summary>
        internal Uri Source
        {
            get
            {
                VerifyAPI();

                return _sourceUri;
            }
        }

        /// <summary>
        /// Internal Get Volume
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is critical because it calls into unmanged code and accesses _nativeMedia
        ///     TreatAsSafe: This is safe because the critical resource is not exposed and
        ///     returning volume ratio is safe and so also is setting it
        /// </SecurityNote>
        internal double Volume
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();

                return _volume;
            }
            [SecurityCritical, SecurityTreatAsSafe]
            set
            {
                VerifyAPI();
                if (Double.IsNaN(value))
                {
                    throw new ArgumentException(SR.Get(SRID.ParameterValueCannotBeNaN), "value");
                }

                if (DoubleUtil.GreaterThanOrClose(value, 1))
                {
                    value = 1;
                }
                else if (DoubleUtil.LessThanOrClose(value, 0))
                {
                    value = 0;
                }

                // We only want to set the volume if the current cached volume is not the same
                // No need to do extra work.
                if (!DoubleUtil.AreClose(_volume, value))
                {
                    if (!_muted)
                    {
                        int hr = MILMedia.SetVolume(_nativeMedia, value);

                        HRESULT.Check(hr);

                        // value is changing
                        _volume = value;
                    }
                    else
                    {
                        // If we are muted, cache the volume
                        _volume = value;
                    }
                }
            }
        }

        /// <summary>
        /// Internal Get Balance
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This is accesses the native media.
        ///     TreatAsSafe: This is safe because the act of adjusting the balance is a safe one.
        /// </SecurityNote>
        internal double Balance
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();

                return _balance;
            }
            [SecurityCritical, SecurityTreatAsSafe]
            set
            {
                VerifyAPI();
                if (Double.IsNaN(value))
                {
                    throw new ArgumentException(SR.Get(SRID.ParameterValueCannotBeNaN), "value");
                }

                if (DoubleUtil.GreaterThanOrClose(value, 1))
                {
                    value = 1;
                }
                else if (DoubleUtil.LessThanOrClose(value, -1))
                {
                    value = -1;
                }

                // We only want to set the balance if the current cached balance
                // is not the same. No need to do extra work.
                if (!DoubleUtil.AreClose(_balance, value))
                {
                    int hr = MILMedia.SetBalance(_nativeMedia, value);

                    HRESULT.Check(hr);

                    // value is changing
                    _balance = value;
                }
            }
        }

        /// <summary>
        /// Whether or not scrubbing is enabled
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This accesses the native media.
        ///     TreatAsSafe: This is safe because it's safe to adjust whether or not scrubbing is enabled
        /// </SecurityNote>
        internal bool ScrubbingEnabled
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();
                return _scrubbingEnabled;
            }
            [SecurityCritical, SecurityTreatAsSafe]
            set
            {
                VerifyAPI();
                if (value != _scrubbingEnabled)
                {
                    HRESULT.Check(MILMedia.SetIsScrubbingEnabled(_nativeMedia, value));
                    _scrubbingEnabled = value;
                }
            }
        }

        /// <summary>
        /// Internal Get Mute
        /// </summary>
        internal bool IsMuted
        {
            get
            {
                VerifyAPI();
                return _muted;
            }
            set
            {
                VerifyAPI();

                // we need to store the volume since this.Volume will change the cached value
                double volume = _volume;

                if (value && !_muted)
                {
                    // Going from Unmuted -> Muted

                    // Set the volume to 0
                    this.Volume = 0;
                    _muted = true;

                    // make sure cached volume is previous value
                    _volume = volume;
                }
                else if (!value && _muted)
                {
                    // Going from Muted -> Unmuted

                    _muted = false;

                    // set cached volume to 0 since this. Volume will only change volume
                    // if cached volume and new volume differ
                    _volume = 0;

                    // set volume to old cached value, which will also update our current cached value
                    this.Volume = volume;
                }
            }
        }

        /// <SecurityNote>
        ///     Critical: This is critical it calls into unmanged code and accesses _nativeMedia
        ///     TreatAsSafe: This is safe because the critical resource is not exposed and
        ///     returning natural duration is safe
        /// </SecurityNote>
        internal Duration NaturalDuration
        {
            [SecurityCritical, SecurityTreatAsSafe]
            get
            {
                VerifyAPI();

                long mediaLength = 0;
                HRESULT.Check(MILMedia.GetMediaLength(_nativeMedia, ref mediaLength));
                if (mediaLength == 0)
                {
                    return Duration.Automatic;
                }
                else
                {
                    return new Duration(TimeSpan.FromTicks(mediaLength));
                }
            }
        }

        /// <summary>
        /// Seek to specified position
        /// </summary>
        internal TimeSpan Position
        {
            set
            {
                VerifyAPI();

                VerifyNotControlledByClock();

                SetPosition(value);
            }
            get
            {
                VerifyAPI();

                return GetPosition();
            }
        }

        /// <summary>
        /// The current speed. This cannot be changed if a clock is controlling this player
        /// </summary>
        internal double SpeedRatio
        {
            get
            {
                VerifyAPI();

                return _speedRatio;
            }
            set
            {
                VerifyAPI();
                VerifyNotControlledByClock();

                if (value < 0)
                {
                    value = 0; // we clamp negative values to 0
                }

                SetSpeedRatio(value);
            }
        }

        /// <summary>
        /// The dispatcher, this is actually derived from the media player
        /// on construction.
        /// </summary>
        internal Dispatcher Dispatcher
        {
            get
            {
                return _dispatcher;
            }
        }


        #endregion

        #region EventHandlers

        /// <summary>
        /// Raised when there is an error opening or playing media
        /// </summary>
        internal event EventHandler<ExceptionEventArgs> MediaFailed
        {
            add
            {
                VerifyAPI();
                _mediaEventsHelper.MediaFailed += value;
            }
            remove
            {
                VerifyAPI();
                _mediaEventsHelper.MediaFailed -= value;
            }
        }

        /// <summary>
        /// Raised when the media has been opened.
        /// </summary>
        internal event EventHandler MediaOpened
        {
            add
            {
                VerifyAPI();

                _mediaOpenedHelper.AddEvent(value);
            }
            remove
            {
                VerifyAPI();

                _mediaOpenedHelper.RemoveEvent(value);
            }
        }


        /// <summary>
        /// Raised when the media has finished.
        /// </summary>
        internal event EventHandler MediaEnded
        {
            add
            {
                VerifyAPI();
                _mediaEventsHelper.MediaEnded += value;
            }
            remove
            {
                VerifyAPI();
                _mediaEventsHelper.MediaEnded -= value;
            }
        }


        /// <summary>
        /// Raised when media begins buffering.
        /// </summary>
        internal event EventHandler BufferingStarted
        {
            add
            {
                VerifyAPI();
                _mediaEventsHelper.BufferingStarted += value;
            }
            remove
            {
                VerifyAPI();
                _mediaEventsHelper.BufferingStarted -= value;
            }
        }


        /// <summary>
        /// Raised when media finishes buffering.
        /// </summary>
        internal event EventHandler BufferingEnded
        {
            add
            {
                VerifyAPI();

                _mediaEventsHelper.BufferingEnded += value;
            }
            remove
            {
                VerifyAPI();

                _mediaEventsHelper.BufferingEnded -= value;
            }
        }

        /// <summary>
        /// Raised when a script command embedded in the media is encountered.
        /// </summary>
        internal event EventHandler<MediaScriptCommandEventArgs> ScriptCommand
        {
            add
            {
                VerifyAPI();
                _mediaEventsHelper.ScriptCommand += value;
            }
            remove
            {
                VerifyAPI();
                _mediaEventsHelper.ScriptCommand -= value;
            }
        }

        /// <summary>
        /// Raised when a new frame in the media is encountered, we only
        /// send one new frame per AddRefOnChannel in synchronous mode only.
        /// </summary>
        internal event EventHandler NewFrame
        {
            add
            {
                VerifyAPI();

                _newFrameHelper.AddEvent(value);
            }

            remove
            {
                VerifyAPI();

                _newFrameHelper.RemoveEvent(value);
            }
        }

        #endregion

        #region Clock dependent properties and methods

        /// <summary>
        /// The clock driving this instance of media
        /// </summary>
        internal MediaClock Clock
        {
            get
            {
                VerifyAPI();
                return _mediaClock;
            }
        }

        internal
        void
        SetClock(
            MediaClock  clock,
            MediaPlayer player
            )
        {
            VerifyAPI();
            MediaClock oldClock = _mediaClock;
            MediaClock newClock = clock;

            // Avoid infinite loops
            if (oldClock != newClock)
            {
                _mediaClock = newClock;

                // Disassociate the old clock
                if (oldClock != null)
                {
                    oldClock.Player = null;
                }

                // Associate the new clock;
                if (newClock != null)
                {
                    newClock.Player = player;
                }

                // According to the spec, setting the Clock to null
                // should set the Source to null
                if (newClock == null)
                {
                    Open(null);
                }
            }
        }

        /// <summary>
        /// Open the media, at this point the underlying native resources are
        /// created. The media player cannot be controlled when it isn't opened.
        /// </summary>
        internal
        void
        Open(
            Uri      source
            )
        {
            VerifyAPI();
            VerifyNotControlledByClock();

            SetSource(source);

            // Media fails to resume playback after setting source property to the same value.
			// We workaround this by resuing one instance of MediaElement and
            // calling play() wont result in seek to zero, Media Freezes.  Ensure
            // we set Media to play from the beginning.
            SetPosition(TimeSpan.Zero);
        }

        /// <summary>
        /// Begin playback. This operation is not allowed if a clock is
        /// controlling this player
        /// </summary>
        internal void Play()
        {
            VerifyAPI();
            VerifyNotControlledByClock();

            _paused = false;
            PrivateSpeedRatio = SpeedRatio;
        }

        /// <summary>
        /// Halt playback at current position. This operation is not allowed if
        /// a clock is controlling this player
        /// </summary>
        internal void Pause()
        {
            VerifyAPI();
            VerifyNotControlledByClock();

            _paused = true;
            PrivateSpeedRatio = 0;
        }

        /// <summary>
        /// Halt playback and seek to the beginning of media. This operation is
        /// not allowed if a clock is controlling this player
        /// </summary>
        internal void Stop()
        {
            VerifyAPI();
            VerifyNotControlledByClock();

            Pause();
            Position = TimeSpan.FromTicks(0);
        }

        /// <summary>
        /// Closes the underlying media. This de-allocates all of the native resources in
        /// the media. The mediaplayer can be opened again by calling the Open method.
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This calls into unmanaged code and also acceses _nativemedia.
        ///     TreatAsSafe: Intrinsically safe to close media.
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        internal
        void
        Close()
        {
            VerifyAPI();

            VerifyNotControlledByClock();

            HRESULT.Check(MILMedia.Close(_nativeMedia));

            //
            // Once we successfully close, we don't have a clock anymore.
            // Assign the property so that the clock is disconnected from the
            // player as well as the player from the clock.
            //
            SetClock(null, null);

            Init();
        }

        /// <summary>
        /// Sends a command to play the given media.
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This calls into unmanaged code and also acceses _nativemedia.
        ///     TreatAsSafe: Media Command merely binds resource to native player.
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        internal
        void
        SendCommandMedia(
            DUCE.Channel            channel,
            DUCE.ResourceHandle     handle,
            bool                    notifyUceDirectly
            )
        {
            SendMediaPlayerCommand(
                channel,
                handle,
                notifyUceDirectly
                );

            //
            // Independently, tell the native media that we need to update the UI, the
            // reason we do this directly through the player is that effects can immediately
            // remove the channel on us and hence media might not get a chance to see
            // the media player resource.
            //
            if (!notifyUceDirectly)
            {
                NeedUIFrameUpdate();
            }
        }

        /// <summary>
        /// Sends a request to the media player to reserve a UI frame for notification.
        /// </summary>
        /// <SecurityNote>
        ///     Critical: This calls into unmanaged code and also acceses _nativemedia.
        ///     TreatAsSafe: Asking for a frame update is inherently safe.
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        private
        void
        NeedUIFrameUpdate()
        {
            VerifyAPI();

            HRESULT.Check(MILMedia.NeedUIFrameUpdate(_nativeMedia));
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Create the unmanaged media resources
        /// </summary>
        /// <SecurityNote>
        /// Critical - calls unmanaged code, access pointer parameters. It instantiates
        ///            windows media player
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        private void CreateMedia(MediaPlayer mediaPlayer)
        {
            CheckMediaDisabledFlags();

            SafeMILHandle unmanagedProxy = null;
            MediaEventsHelper.CreateMediaEventsHelper(mediaPlayer, out _mediaEventsHelper, out unmanagedProxy);
            try
            {
                using (FactoryMaker myFactory = new FactoryMaker())
                {
                    HRESULT.Check(UnsafeNativeMethods.MILFactory2.CreateMediaPlayer(
                            myFactory.FactoryPtr,
                            unmanagedProxy,
                            SecurityHelper.CallerHasMediaPermission(MediaPermissionAudio.AllAudio, MediaPermissionVideo.AllVideo, MediaPermissionImage.NoImage),
                            out _nativeMedia
                            ));
                }
            }
            catch
            {
                if (_nativeMedia != null && !_nativeMedia.IsInvalid)
                {
                    _nativeMedia.Close();
                }

                throw;
            }

            _helper = new Helper(_nativeMedia);
            AppDomain.CurrentDomain.ProcessExit += _helper.ProcessExitHandler;
        }

        /// <summary>
        /// Open Media
        /// </summary>
        /// <SecurityNote>
        /// Critical - access critical resource (_nativeMedia), access local file system
        /// and also stores the path to base directory in a local variable. It also asserts
        /// to allow FileIO to local directory. It calls GetBaseDirectory, that returns sensitive information.
        /// TreatAsSafe: This path is sent to the unmanaged layer to be opened. Also it demands
        /// fileio for absolute paths and web permissions for files on a server. It only lets you access
        /// files in current directory and does not expose the location of current directory
        /// </SecurityNote>
        [SecurityCritical,SecurityTreatAsSafe]
        private void OpenMedia(Uri source)
        {
            string toOpen = null;

            if (source != null && source.IsAbsoluteUri && source.Scheme == PackUriHelper.UriSchemePack)
            {
                try
                {
                    source = BaseUriHelper.ConvertPackUriToAbsoluteExternallyVisibleUri(source);
                }
                catch (InvalidOperationException)
                {
                    source = null;
                    _mediaEventsHelper.RaiseMediaFailed(new System.NotSupportedException(SR.Get(SRID.Media_PackURIsAreNotSupported, null)));
                }
            }

            // Setting a null source effectively disconects the MediaElement.
            if (source != null)
            {
                // keep whether we asserted permissions or not
                bool elevated = false;

                // get the base directory of the application; never expose this
                Uri appBase = SecurityHelper.GetBaseDirectory(AppDomain.CurrentDomain);

                // this extracts the URI to open
                Uri uriToOpen = ResolveUri(source, appBase);

                // access is allowed in the following cases (only 1 & 2 require elevation):
                // 1) to any HTTPS media if app is NOT coming from HTTPS
                // 2) to URI in the current directory of the fusion cache
                // 3) to site of origin media
                if (SecurityHelper.AreStringTypesEqual(uriToOpen.Scheme, Uri.UriSchemeHttps))
                {
                    // target is HTTPS. Then, elevate ONLY if we are NOT coming from HTTPS (=XDomain HTTPS app to HTTPS media disallowed)
                    Uri appDeploymentUri = SecurityHelper.ExtractUriForClickOnceDeployedApp();
                    if (!SecurityHelper.AreStringTypesEqual(appDeploymentUri.Scheme, Uri.UriSchemeHttps))
                    {
                        new WebPermission(NetworkAccess.Connect, BindUriHelper.UriToString(uriToOpen)).Assert();
                        elevated = true;
                    }
                }
                else
                {
                    // elevate to allow access to media in the app's directory in the fusion cache.
                    new FileIOPermission(FileIOPermissionAccess.Read, appBase.LocalPath).Assert();// BlessedAssert
                    elevated = true;
                }

                // demand permissions. if demands succeds, it means we are in one of the cases above.
                try
                {
                    toOpen  = DemandPermissions(uriToOpen);
                }
                finally
                {
                    if (elevated)
                    {
                        CodeAccessPermission.RevertAssert();
                    }
                }
            }
            else
            {
                toOpen = null;
            }

            // We pass in exact same URI for which we demanded permissions so that we can be sure
            // there is no discrepancy between the two.
            HRESULT.Check(MILMedia.Open(_nativeMedia, toOpen));
        }

        /// <SecurityNote>
        ///     Critical: This code elevates to read registry
        ///     TreatAsSafe: Detecting whether media is disabled is a safe operation
        /// </SecurityNote>
        [SecurityCritical,SecurityTreatAsSafe]
        private void CheckMediaDisabledFlags()
        {
            if (SafeSecurityHelper.IsFeatureDisabled(SafeSecurityHelper.KeyToRead.MediaAudioOrVideoDisable))
            {
                // in case the registry key is '1' then demand
                //Demand media permission here for Video or Audio
                // Issue: 1232606 need to fix once clr has the media permissions
                SecurityHelper.DemandMediaPermission(MediaPermissionAudio.AllAudio,
                                                     MediaPermissionVideo.AllVideo,
                                                     MediaPermissionImage.NoImage);
            }
        }

        /// <SecurityNote>
        ///     Critical: This code returns the base directory of the app as a URI
        ///     IT constructs a Uri based on relative and absolute URI
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        private Uri ResolveUri(Uri uri, Uri appBase)
        {
            if (uri.IsAbsoluteUri)
            {
                return uri;
            }
            else
            {
                return new Uri(appBase, uri);
            }
        }

        // returns the exact string on which we demanded permissions

        /// <SecurityNote>
        ///     Critical: This code is used to safeguard against various forms of attacks
        ///     to restrict access to loose file passed as relative path in the application
        ///     base direcory
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        private string DemandPermissions(Uri absoluteUri)
        {
            Debug.Assert(absoluteUri.IsAbsoluteUri);
            string toOpen = BindUriHelper.UriToString(absoluteUri);
            int targetZone = SecurityHelper.MapUrlToZoneWrapper(absoluteUri);

            if (targetZone == NativeMethods.URLZONE_LOCAL_MACHINE)
            {
                // go here only for files and not for UNC
                if (absoluteUri.IsFile)
                {
                    // Please note this pattern is unique and NEEDS TO EXIST , it prevents
                    // access to any folder but the one where the app is running from.
                    // PLEASE DO NOT REMOVE THIS DEMAND AND THE ASSERT IN THE CALLING CODE
                    toOpen = absoluteUri.LocalPath;
                    (new FileIOPermission(FileIOPermissionAccess.Read, toOpen)).Demand();
                }
            }
            else //Any other zone
            {
                // UNC path pointing to a file (We filter for `http://intranet)
                if (absoluteUri.IsFile && absoluteUri.IsUnc)
                {
                    // perform checks for UNC content
                    SecurityHelper.EnforceUncContentAccessRules(absoluteUri);

                    // In this case we first check to see if the consumer has media permissions for
                    // safe media (Site of Origin + Cross domain).
                    if (!SecurityHelper.CallerHasMediaPermission(MediaPermissionAudio.SafeAudio,
                                                                 MediaPermissionVideo.SafeVideo,
                                                                 MediaPermissionImage.NoImage))
                    {
                        // if he does not then we demand web permission to allow access only to site of origin
                        (new FileIOPermission(FileIOPermissionAccess.Read, toOpen)).Demand();
                    }
}
                else // Any other path
                {
                    // In this case we first check to see if the consumer has media permissions for
                    // safe media (Site of Origin + Cross domain).
                    if (absoluteUri.Scheme != Uri.UriSchemeHttps)
                    {
                        //accessing non https content from an https app is disallowed
                        SecurityHelper.BlockCrossDomainForHttpsApps(absoluteUri);
                        if (!SecurityHelper.CallerHasMediaPermission(MediaPermissionAudio.SafeAudio,
                                                                     MediaPermissionVideo.SafeVideo,
                                                                     MediaPermissionImage.NoImage))
                        {
                            // if he does not then we demand web permission to allow access only to site of origin
                            (new WebPermission(NetworkAccess.Connect, toOpen)).Demand();
                        }
                    }
                    else// This is the case where target content is HTTPS
                    {
                        (new WebPermission(NetworkAccess.Connect, toOpen)).Demand();
                    }
}
            }

            return toOpen;
        }

        /// <summary>
        /// Seek to specified position (in 100 nanosecond ticks)
        /// </summary>
        /// <SecurityNote>
        /// Critical - access critical resource (_nativeMedia)
        /// TreatAsSafe - critical resource isn't modified or handed out
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        internal void SetPosition(TimeSpan value)
        {
            VerifyAPI();

            HRESULT.Check(MILMedia.SetPosition(_nativeMedia, value.Ticks));
        }

        /// <summary>
        /// get the current position (in 100 nanosecond ticks)
        /// </summary>
        /// <SecurityNote>
        /// Critical - access critical resource (_nativeMedia)
        /// TreatAsSafe - critical resource isn't modified or handed out
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        private TimeSpan GetPosition()
        {
            VerifyAPI();

            long position = 0;
            HRESULT.Check(MILMedia.GetPosition(_nativeMedia, ref position));
            return TimeSpan.FromTicks(position);
        }

        /// <SecurityNote>
        /// Critical - access critical resource (_nativeMedia)
        /// TreatAsSafe - critical resource isn't modified or handed out
        /// </SecurityNote>
        private double PrivateSpeedRatio
        {
            [SecurityCritical, SecurityTreatAsSafe]
            set
            {
                VerifyAPI();

                if (Double.IsNaN(value))
                {
                    throw new ArgumentException(SR.Get(SRID.ParameterValueCannotBeNaN), "value");
                }

                HRESULT.Check(MILMedia.SetRate(_nativeMedia, value));
            }
        }

        //
        // Set the current speed.
        //
        internal void SetSpeedRatio(double value)
        {
            _speedRatio = value;

            //
            // We don't change the speed if we are paused, unless we are in
            // clock mode, which overrides paused mode.
            //
            if (!_paused || _mediaClock != null)
            {
                PrivateSpeedRatio = _speedRatio;
            }
        }

        /// <summary>
        /// Sets the source of the media (and opens it), without checking whether
        /// we are under clock control. This is called by the clock.
        /// </summary>
        internal
        void
        SetSource(
            Uri      source
            )
        {
            if (source != _sourceUri)
            {
                OpenMedia(source);

                //
                // Only assign the source uri if the OpenMedia succeeds.
                //
                _sourceUri = source;
            }
        }


        /// <summary>
        /// Verifies this object is in an accessible state, and that we
        /// are being called from the correct thread. This method should
        /// be the first thing called from any internal method.
        /// </summary>
        /// <SecurityNote>
        /// Critical - access critical resource (_nativeMedia)
        /// TreatAsSafe - critical resource isn't modified or handed out
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        private void VerifyAPI()
        {
            //
            // We create _nativeMedia in the constructor, so it should always
            // be initialized.
            //
            Debug.Assert(_nativeMedia != null && !_nativeMedia.IsInvalid);

            //
            // We only allow calls to any media object on the UI thread.
            //
            _dispatcher.VerifyAccess();

            if (_nativeMedia == null || _nativeMedia.IsInvalid)
            {
                throw new System.NotSupportedException(SR.Get(SRID.Image_BadVersion));
            }
        }

        /// <summary>
        /// Verifies that this player is not currently controlled by a clock. Some actions are
        /// invalid while we are under clock control.
        /// </summary>
        private
        void
        VerifyNotControlledByClock()
        {
            if (Clock != null)
            {
                throw new InvalidOperationException(SR.Get(SRID.Media_NotAllowedWhileTimingEngineInControl));
            }
        }

        /// <summary>
        /// SendMediaPlayerCommand
        /// </summary>              SecurityNote
        /// <SecurityNote>
        /// Critical - access critical resource (_nativeMedia)
        /// TreatAsSafe - critical resource is treated like any other image.
        /// </SecurityNote>
        [SecurityCritical, SecurityTreatAsSafe]
        private
        void
        SendMediaPlayerCommand(
            DUCE.Channel            channel,
            DUCE.ResourceHandle     handle,
            bool                    notifyUceDirectly
            )
        {
//
// This is an interrop call, but, it does not set a last error being a COM call. So, suppress the
// presharp warning about losing last error.
//
#pragma warning disable 6523

            //
            // AddRef to ensure the media player stays alive during transport, even if the
            // MediaPlayer goes away.  The slave video resource takes ownership of this AddRef.
            // Note there is still a gray danger zone here -- if the channel command is lost
            // this reference won't be cleaned up.
            //
            // MediaPlayer: AddRef on nativeMedia may be lost if the channel send command fails
            //
            // There is no point in addrefing the native media if we are going remote since
            // we will send null.
            //
            UnsafeNativeMethods.MILUnknown.AddRef(_nativeMedia);

            channel.SendCommandMedia(
                handle,
                _nativeMedia,
                notifyUceDirectly
                );

#pragma warning restore 6523
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// When a new frame is received, we need to check if it is remote and then acquire the new frame
        /// from the composition synchronously before passing the NewFrame event up to the
        /// Media player.
        /// </summary>
        private
        void
        OnNewFrame(
            object          sender,
            EventArgs       args
            )
        {
            _newFrameHelper.InvokeEvents(sender, args);
        }

        /// <summary>
        /// Fired when the media is opened, we can't open our capture resources until the media is opened
        /// because we won't know the size of the media.
        /// </summary>
        private
        void
        OnMediaOpened(
            object          sender,
            EventArgs       args
            )
        {
            _mediaOpenedHelper.InvokeEvents(sender, args);
        }

        #endregion


        #region Data Members

        /// <summary>
        /// Current volume (ranges from 0 to 1)
        /// </summary>
        private double _volume;

        /// <summary>
        /// Current balance (ranges from -1 (left) to 1 (right) )
        /// </summary>
        private double _balance;

        /// <summary>
        /// Current state of mute
        /// </summary>
        private bool _muted;

        /// <summary>
        /// Whether or not scrubbin is enabled
        /// </summary>
        private bool _scrubbingEnabled;

        /// <summary>
        /// Unamanaged Media object
        /// </summary>
        /// <SecurityNote>
        /// Critical - this is a pointer to an unmanaged object that methods are called directly on
        /// </SecurityNote>
        [SecurityCritical]
        private SafeMediaHandle _nativeMedia;

        private MediaEventsHelper _mediaEventsHelper;

        /// <summary>
        /// Default volume
        /// </summary>
        private const double DEFAULT_VOLUME = 0.5;

        /// <summary>
        /// Default balance
        /// </summary>
        private const double DEFAULT_BALANCE = 0;

        private double _speedRatio;
        private bool _paused;

        private Uri _sourceUri;

        private MediaClock _mediaClock = null;

        private Dispatcher _dispatcher = null;

        private UniqueEventHelper _newFrameHelper = new UniqueEventHelper();
        private UniqueEventHelper _mediaOpenedHelper = new UniqueEventHelper();

        private const float _defaultDevicePixelsPerInch = 96.0F;

        private Helper _helper;

        /// <summary>
        /// A separate class is needed to register for the ProcessExit event
        /// because MediaPlayerState holds a strong reference to _nativeMedia.
        /// If a MediaPlayerState method was registered for the ProcessExit
        /// event then MediaPlayerState would not be garbage collected until
        /// ProcessExit time.
        /// </summary>
        private class Helper
        {
            /// <SecurityNote>
            /// Critical - this is a weak reference to a pointer to an unmanaged object
            /// on which methods are called directly
            /// </SecurityNote>
            [SecurityCritical]
            private WeakReference _nativeMedia;

            /// <SecurityNote>
            /// Accesses weak reference to pointer
            /// </SecurityNote>
            [SecurityCritical]
            internal
            Helper(
                SafeMediaHandle nativeMedia
                )
            {
                _nativeMedia = new WeakReference(nativeMedia);
            }

            /// <SecurityNote>
            /// Accesses weak reference to pointer, calls unmanaged code
            /// </SecurityNote>
            [SecurityCritical]
            internal
            void
            ProcessExitHandler(
                object sender,
                EventArgs args
                )
            {
                SafeMediaHandle nativeMedia = (SafeMediaHandle)_nativeMedia.Target;
                if (nativeMedia != null)
                {
                    MILMedia.ProcessExitHandler(nativeMedia);
                }
            }
        };
        #endregion
    }

    #endregion
};

