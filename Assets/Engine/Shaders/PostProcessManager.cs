using Engine.Scripts.Utils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Engine.Shaders
{
    /// <summary>
    ///     Singleton wrapper around a URP <see cref="Volume" /> that exposes simple methods
    ///     to control lift/gamma/gain and color adjustments for post-processing.
    /// </summary>
    public class PostProcessManager : Singleton<PostProcessManager>
    {
        /// <summary>
        ///     Volume that contains the post-processing profile used for adjustments.
        /// </summary>
        public Volume volume;

        private ColorAdjustments _colorAdjustments;

        private LiftGammaGain _liftGammaGain;

        private VolumeProfile _profile;

        private void OnEnable()
        {
            volume.profile = _profile;
            volume.profile.TryGet(out _liftGammaGain);
            volume.profile.TryGet(out _colorAdjustments);
        }

        /// <summary>
        ///     Convenience overload that applies the same <paramref name="all" /> vector
        ///     to lift, gamma and gain.
        /// </summary>
        /// <param name="all">Vector used for lift, gamma and gain.</param>
        public void SetLiftGammaGain(Vector4 all)
        {
            SetLiftGammaGain(all, all, all);
        }

        /// <summary>
        ///     Enables and sets the lift, gamma and gain values on the active <see cref="LiftGammaGain" /> override.
        /// </summary>
        /// <param name="lift">Lift value to apply.</param>
        /// <param name="gamma">Gamma value to apply.</param>
        /// <param name="gain">Gain value to apply.</param>
        public void SetLiftGammaGain(Vector4 lift, Vector4 gamma, Vector4 gain)
        {
            if (!_liftGammaGain) return;
            _liftGammaGain.active = true;
            _liftGammaGain.lift.value = lift;
            _liftGammaGain.gamma.value = gamma;
            _liftGammaGain.gain.value = gain;
        }

        /// <summary>
        ///     Disables and resets the lift/gamma/gain override to default values.
        /// </summary>
        public void ResetLiftGammaGain()
        {
            if (!_liftGammaGain || !_liftGammaGain.active) return;
            _liftGammaGain.active = false;
            _liftGammaGain.lift.value = Vector4.zero;
            _liftGammaGain.gamma.value = Vector4.one;
            _liftGammaGain.gain.value = Vector4.one;
        }

        /// <summary>
        ///     Enables and sets contrast and saturation on the active <see cref="ColorAdjustments" /> override.
        /// </summary>
        /// <param name="contrast">Contrast value to apply.</param>
        /// <param name="saturation">Saturation value to apply.</param>
        public void SetColorAdjustments(float contrast, float saturation)
        {
            if (!_colorAdjustments) return;
            _colorAdjustments.active = true;
            _colorAdjustments.contrast.value = contrast;
            _colorAdjustments.saturation.value = saturation;
        }

        /// <summary>
        ///     Disables and resets color adjustments to neutral values.
        /// </summary>
        public void ResetColorAdjustments()
        {
            if (!_colorAdjustments || !_colorAdjustments.active) return;
            _colorAdjustments.active = false;
            _colorAdjustments.contrast.value = 0f;
            _colorAdjustments.saturation.value = 0f;
        }
    }
}