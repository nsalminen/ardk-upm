// Copyright 2022-2025 Niantic.

using System;
using System.IO;
using Niantic.Lightship.AR.PAM;
using Niantic.Lightship.AR.Utilities.Logging;
using Niantic.Lightship.AR.Utilities.Profiling;
using Niantic.Lightship.Utilities.UnityAssets;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace Niantic.Lightship.AR.Subsystems.Playback
{
    internal class PlaybackDatasetReader
    {
        private readonly PlaybackDataset _dataset;
        private int _currentFrameIndex;
        private readonly int _startFrame;
        private readonly int _endFrame;
        private int _lastLoadedImageFrameNumber = -1;
        private byte[] _imageBytes;

        private readonly bool _loopInfinitely;

        // Values to track for going back and forth when looping to not have jumps in pose and timestamps
        private bool _goingForward = true;
        private double _timestampLoopOffset;

        private const string TraceCategory = "PlaybackDatasetReader";

        internal Action FrameChanged;

        public PlaybackDatasetReader(PlaybackDataset dataset, bool loopInfinitely = false, int? startFrame = null, int? endFrame = null)
        {
            _dataset = dataset ?? throw new ArgumentNullException(nameof(dataset));
            _loopInfinitely = loopInfinitely;

            if (startFrame < 0 || startFrame >= _dataset.FrameCount)
            {
                Log.Warning($"Invalid start frame {startFrame} for dataset {_dataset.DatasetPath}. Defaulting to 0.");
                startFrame = 0;
            }

            if (endFrame < 0 || endFrame <= startFrame || endFrame >= _dataset.FrameCount)
            {
                Log.Warning($"Invalid end frame {endFrame} for dataset {_dataset.DatasetPath}. Defaulting to {_dataset.FrameCount - 1}.");
                endFrame = _dataset.FrameCount - 1;
            }

            _startFrame = startFrame ?? 0;
            _currentFrameIndex = startFrame != null ? startFrame.Value - 1 : -1;
            _endFrame = endFrame ?? _dataset.FrameCount - 1;
        }

        // This function will try to go to the next frame depending on the current auto moving direction (_goingForward)
        public bool TryMoveToNextFrame()
        {
            bool reachedNextFrame = _goingForward ? TryMoveForward() : TryMoveBackward();

            if (!reachedNextFrame)
            {
                if (_loopInfinitely)
                {
                    _goingForward = !_goingForward;
                    return TryMoveToNextFrame();
                }

                _finished = true;
                return false;
            }

            return true;
        }

        // This function will try to go to the previous frame depending on the current auto moving direction (_goingForward)
        public bool TryMoveToPreviousFrame()
        {
            bool reachedNextFrame = _goingForward ? TryMoveBackward() : TryMoveForward();

            if (!reachedNextFrame)
            {
                if (_loopInfinitely)
                {
                    _goingForward = !_goingForward;
                    return TryMoveToPreviousFrame();
                }

                _finished = true;
                return false;
            }

            return true;
        }

        // This function will try to go to the next forward frame
        public bool TryMoveForward()
        {
            // We have reached the end of the dataset or the user-defined end frame
            if (_currentFrameIndex >= _endFrame)
            {
                return false;
            }

            _currentFrameIndex++;

            Log.Debug("Playback moved forward to frame " + _dataset.Frames[_currentFrameIndex].Sequence);
            FrameChanged?.Invoke();

            return true;
        }

        // This function will try to go to the next backward frame, so the t-1 frame while incrementing the timing offset
        public bool TryMoveBackward()
        {
            // We have reached the start of the dataset or the user-defined start frame
            if (_currentFrameIndex <= _startFrame)
            {
                return false;
            }

            // To calculate the new timestamp, we need to know the state of the frame we are leaving
            var previousFrameIndex = _currentFrameIndex;
            var previousFrameRawTimestamp = _dataset.Frames[previousFrameIndex].TimestampInSeconds;
            var totalPreviousTimestamp = previousFrameRawTimestamp + _timestampLoopOffset;

            _currentFrameIndex--;

            var currentFrameRawTimestamp = _dataset.Frames[_currentFrameIndex].TimestampInSeconds;

            // The time delta between the two frames in the original dataset
            var timeDelta = previousFrameRawTimestamp - currentFrameRawTimestamp;

            // The new timestamp should be the last reported timestamp plus the delta
            var newTotalTimestamp = totalPreviousTimestamp + timeDelta;

            // The offset is the difference between our desired total timestamp and the raw timestamp of the current frame
            _timestampLoopOffset = newTotalTimestamp - currentFrameRawTimestamp;

            Log.Debug("Playback moved back to frame " + _dataset.Frames[_currentFrameIndex].Sequence);
            FrameChanged?.Invoke();

            return true;
        }

        private bool _finished;
        public bool Finished => _finished;

        public string GetDatasetPath() => _dataset.DatasetPath;

        public bool GetAutofocusEnabled() => _dataset.AutofocusEnabled;

        public Vector2Int GetImageResolution() => _dataset.Resolution;

        public Vector2Int GetDepthResolution() => _dataset.DepthResolution;

        public int GetFramerate() => _dataset.FrameRate;

        public int GetTotalFrameCount() => _dataset.FrameCount;

        public bool GetLocationServicesEnabled() => _dataset.LocationServicesEnabled;

        public bool GetCompassEnabled() => _dataset.CompassEnabled;

        public bool GetIsLidarAvailable() => _dataset?.LidarEnabled ?? false;

        public Matrix4x4 GetCurrentPose() => CurrFrame?.Pose ?? MatrixUtils.InvalidMatrix;

        public Matrix4x4 GetCurrentProjectionMatrix() => CurrFrame?.ProjectionMatrix ?? MatrixUtils.InvalidMatrix;

        public XRCameraIntrinsics GetCurrentIntrinsics() => CurrFrame?.Intrinsics ?? default;

        public CameraIntrinsicsCStruct GetCurrentIntrinsicsCstruct() => CurrFrame?.IntrinsicsCStruct ?? default;

        public byte[] GetCurrentImageData() =>
            CurrFrame != null ? ReadImageData(_currentFrameIndex, CurrFrame.ImagePath) : null;

        public string GetCurrentImagePath() => CurrFrame?.ImagePath;

        public string GetCurrentDepthPath() => CurrFrame?.DepthPath;

        public string GetCurrentDepthConfidencePath() => CurrFrame?.DepthConfidencePath;

        public TrackingState GetCurrentTrackingState() => CurrFrame?.TrackingState ?? TrackingState.None;

        public double GetCurrentTimestampInSeconds() => CurrFrame?.TimestampInSeconds ?? 0;

        public int CurrentFrameIndex => _currentFrameIndex;

        public int StartFrame => _startFrame;

        public int EndFrame => _endFrame;


        public PlaybackDataset.FrameMetadata GetFrame(int index)
        {
            if (index < 0 || index >= _dataset.FrameCount)
            {
                throw new ArgumentOutOfRangeException($"Index {index} is out of range for dataset with {_dataset.FrameCount} frames");
            }

            return _dataset.Frames[index];
        }

        public int GetNextFrameIndex()
        {
            if (_goingForward)
            {
                if (_currentFrameIndex >= _endFrame)
                    return _loopInfinitely ? _currentFrameIndex - 1 : _currentFrameIndex;

                return _currentFrameIndex + 1;
            }
            else // Moving backwards
            {
                if (_currentFrameIndex <= _startFrame)
                    return _loopInfinitely ? _currentFrameIndex + 1 : _currentFrameIndex;

                return _currentFrameIndex - 1;
            }
        }

        public void Reset()
        {
            _currentFrameIndex = _startFrame - 1;
            _timestampLoopOffset = 0;
            _goingForward = true;
            _finished = false;
        }

        public PlaybackDataset.FrameMetadata CurrFrame
        {
            get
            {
                if (_currentFrameIndex < _startFrame || _currentFrameIndex > _endFrame)
                {
                    return null;
                }

                var frame = new PlaybackDataset.FrameMetadata(_dataset.Frames[_currentFrameIndex], _timestampLoopOffset);
                return frame;
            }
        }

        private byte[] ReadImageData(int frameNumber, string fileName)
        {
            if (_lastLoadedImageFrameNumber == frameNumber)
            {
                return _imageBytes;
            }

            ProfilerUtility.EventBegin(TraceCategory, "ReadImageData");
            var filePath = Path.Combine(_dataset.DatasetPath, fileName);
            _imageBytes = FileUtilities.GetAllBytes(filePath);
            _lastLoadedImageFrameNumber = frameNumber;
            ProfilerUtility.EventEnd(TraceCategory, "ReadImageData");

            return _imageBytes;
        }
    }
}
