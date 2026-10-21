using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Baird.ViewModels;
using System;

namespace Baird.Controls
{
    public partial class LottieSpikeControl : UserControl
    {
        private LottieSpikeViewModel? _boundViewModel;
        private bool _isInternalSliderUpdate;

        public LottieSpikeControl()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;

            if (SeekSlider != null)
            {
                SeekSlider.ValueChanged += OnSeekSliderValueChanged;
            }
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            if (_boundViewModel != null)
            {
                _boundViewModel.RequestPlay -= OnRequestPlay;
                _boundViewModel.RequestPause -= OnRequestPause;
                _boundViewModel.RequestRestart -= OnRequestRestart;
                _boundViewModel.RequestSeek -= OnRequestSeek;
                _boundViewModel = null;
            }

            if (DataContext is LottieSpikeViewModel vm)
            {
                _boundViewModel = vm;
                vm.RequestPlay += OnRequestPlay;
                vm.RequestPause += OnRequestPause;
                vm.RequestRestart += OnRequestRestart;
                vm.RequestSeek += OnRequestSeek;
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            if (LottiePlayer != null)
            {
                LottiePlayer.AnimationCompleted += OnAnimationCompleted;
                LottiePlayer.AnimationCompletedRepetition += OnAnimationCompletedRepetition;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (LottiePlayer != null)
            {
                LottiePlayer.AnimationCompleted -= OnAnimationCompleted;
                LottiePlayer.AnimationCompletedRepetition -= OnAnimationCompletedRepetition;
            }

            if (_boundViewModel != null)
            {
                _boundViewModel.RequestPlay -= OnRequestPlay;
                _boundViewModel.RequestPause -= OnRequestPause;
                _boundViewModel.RequestRestart -= OnRequestRestart;
                _boundViewModel.RequestSeek -= OnRequestSeek;
                _boundViewModel = null;
            }

            base.OnDetachedFromVisualTree(e);
        }

        private void OnRequestPlay()
        {
            try
            {
                LottiePlayer?.Resume();
            }
            catch
            {
                LottiePlayer?.Start();
            }
        }

        private void OnRequestPause()
        {
            try
            {
                LottiePlayer?.Pause();
            }
            catch
            {
                // Ignore if not supported in current state
            }
        }

        private void OnRequestRestart()
        {
            try
            {
                LottiePlayer?.SeekToProgress(0f);
                LottiePlayer?.Start();
            }
            catch
            {
                // Ignore if not supported in current state
            }
        }

        private void OnRequestSeek(float progress)
        {
            try
            {
                _isInternalSliderUpdate = true;
                LottiePlayer?.SeekToProgress(progress);
            }
            finally
            {
                _isInternalSliderUpdate = false;
            }
        }

        private void OnSeekSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isInternalSliderUpdate) return;

            try
            {
                var progress = (float)Math.Clamp(e.NewValue, 0.0, 1.0);
                LottiePlayer?.SeekToProgress(progress);
            }
            catch
            {
                // Seek during loading / uninitialized
            }
        }

        private void OnAnimationCompleted(object? sender, EventArgs e)
        {
            _boundViewModel?.OnAnimationFinished();
        }

        private void OnAnimationCompletedRepetition(object? sender, int repetition)
        {
            _boundViewModel?.OnRepetitionCompleted(repetition);
        }
    }
}
