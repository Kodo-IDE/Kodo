// Licensed under the GNU GPL-v3.0
using System;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Kodo;

internal static class AnimationKit
{
    public static bool ReduceMotion { get; set; }

    public static void PresetFade(Visual target, double lift = 0)
    {
        if (target is null) return;
        if (ReduceMotion)
        {
            target.Opacity = 1d;
            target.RenderTransform = null;
            return;
        }
        target.Opacity = 0d;
        if (lift > 0)
            target.RenderTransform = PresetLift(lift);
    }

    public static ITransform? PresetLift(double distance) => Translation(0, distance);

    public static void FadeIn(Visual target, double durationMs = 170, double lift = 0)
    {
        if (target is null) return;
        if (ReduceMotion)
        {
            target.Opacity = 1d;
            target.RenderTransform = null;
            return;
        }
        EnsureOpacityTransition(target, TimeSpan.FromMilliseconds(durationMs));
        target.Opacity = 1d;
        if (lift <= 0) return;
        SlideUp(target, lift, durationMs + 40);
    }

    public static void SlideUp(Visual target, double distance = 12, double durationMs = 220)
    {
        if (target is null) return;
        if (ReduceMotion)
        {
            target.RenderTransform = null;
            return;
        }
        RemoveTransformTransition(target);
        target.RenderTransform = Translation(0, distance);
        Dispatcher.UIThread.Post(() =>
        {
            EnsureTransformTransition(target, TimeSpan.FromMilliseconds(durationMs));
            target.RenderTransform = Translation(0, 0);
        }, DispatcherPriority.Render);
    }

    public static void Pulse(Visual target, double dip = 0.5, int durationMs = 240)
        => PulseCore(target, dip, durationMs);

    public static void PulseAccent(Visual target, int durationMs = 260)
        => PulseCore(target, 0.4, durationMs);

    private static void PulseCore(Visual target, double dip, int durationMs)
    {
        if (target is null || ReduceMotion) return;
        if (target.Opacity < 1d) return;
        EnsureOpacityTransition(target, TimeSpan.FromMilliseconds(110));
        target.Opacity = dip;
        var restoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        restoreTimer.Tick += (_, _) =>
        {
            target.Opacity = 1d;
            restoreTimer.Stop();
        };
        restoreTimer.Start();
    }

    private static TransformOperations Translation(double x, double y)
    {
        var builder = new TransformOperations.Builder(1);
        builder.AppendTranslate(x, y);
        return builder.Build();
    }

    private static void EnsureOpacityTransition(Visual target, TimeSpan duration)
    {
        if (target.Transitions is { } transitions
            && transitions.OfType<DoubleTransition>().Any(t => t.Property == Visual.OpacityProperty))
            return;
        var collection = target.Transitions ?? new Transitions();
        collection.Add(new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = duration,
            Easing = new CubicEaseOut(),
        });
        target.Transitions = collection;
    }

    private static void EnsureTransformTransition(Visual target, TimeSpan duration)
    {
        if (target.Transitions is { } transitions
            && transitions.OfType<TransformOperationsTransition>().Any(t => t.Property == Visual.RenderTransformProperty))
            return;
        var collection = target.Transitions ?? new Transitions();
        collection.Add(new TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty,
            Duration = duration,
            Easing = new CubicEaseOut(),
        });
        target.Transitions = collection;
    }

    private static void RemoveTransformTransition(Visual target)
    {
        if (target.Transitions is not { } transitions) return;
        var kept = transitions
            .Where(t => t is not TransformOperationsTransition x || x.Property != Visual.RenderTransformProperty)
            .ToArray();
        if (kept.Length == transitions.Count) return;
        var collection = new Transitions();
        foreach (var transition in kept)
            collection.Add(transition);
        target.Transitions = collection;
    }
}
