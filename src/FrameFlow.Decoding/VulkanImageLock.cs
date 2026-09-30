// Copyright 2026 Charles Lee
// SPDX-License-Identifier: PolyForm-Small-Business-1.0.0

using FFmpeg.AutoGen.Abstractions;
using FrameFlow.Native.Interop;

namespace FrameFlow.Decoding;

/// <summary>
/// A Vulkan-decoded frame's image, locked for one submission (#535), from
/// <see cref="GpuVideoFrame.TryLockVulkanImage"/>. Disposing it unlocks the frame.
/// </summary>
/// <remarks>
/// <para>
/// FFmpeg's decoder reuses output images as reference pictures, so work on the image has to follow
/// the rules in <c>hwcontext_vulkan.h</c>, which this lock carries out:
/// </para>
/// <list type="number">
/// <item>Lock just before the submission. The properties below are the frame's state under the lock.</item>
/// <item>Record barriers from <see cref="Layout"/> and <see cref="Access"/>.</item>
/// <item>Submit work that waits on <see cref="Semaphore"/> at <see cref="WaitValue"/> and signals it at <see cref="SignalValue"/>.</item>
/// <item>Call <see cref="Commit"/> with the layout and access the submission leaves, then dispose, without waiting for the work.</item>
/// </list>
/// <para>
/// Disposing without <see cref="Commit"/> leaves the frame as it was, which is right when nothing
/// was submitted. Committing without submitting leaves FFmpeg waiting on a value nothing signals.
/// </para>
/// <para>
/// The lock holds a reference to the frame's native data, so it stays valid while locked even if
/// the frame is disposed meanwhile. Dispose it on the thread that took it, and do not hold it across
/// an <see langword="await"/>.
/// </para>
/// </remarks>
public sealed unsafe class VulkanImageLock : IDisposable
{
    private FrameHandle? _handle;
    private readonly AVHWFramesContext* _framesCtx;
    private readonly AVVulkanFramesContext* _vulkanFrames;
    private readonly AVVkFrame* _frame;
    private bool _committed;

    /// <param name="handle">The frame's handle, already add-ref'd; released on dispose.</param>
    /// <param name="framesCtx">The frame's pool.</param>
    /// <param name="frame">The frame's <c>AVVkFrame</c>, already locked; unlocked on dispose.</param>
    internal VulkanImageLock(FrameHandle handle, AVHWFramesContext* framesCtx, AVVkFrame* frame)
    {
        _handle = handle;
        _framesCtx = framesCtx;
        _vulkanFrames = (AVVulkanFramesContext*)framesCtx->hwctx;
        _frame = frame;

        Image = frame->img[0];
        Format = _vulkanFrames->format[0];
        Layout = frame->layout[0];
        Access = frame->access[0];
        Semaphore = frame->sem[0];
        WaitValue = frame->sem_value[0];
        QueueFamily = frame->queue_family[0];
    }

    /// <summary><c>VkImage</c>: one multi-planar image holding every plane.</summary>
    public ulong Image { get; }

    /// <summary>
    /// The image's <c>VkFormat</c>: <c>VK_FORMAT_G8_B8R8_2PLANE_420_UNORM</c> for NV12 and
    /// <c>VK_FORMAT_G10X6_B10X6R10X6_2PLANE_420_UNORM_3PACK16</c> for P010. The pool creates images
    /// with <c>MUTABLE_FORMAT</c> and <c>EXTENDED_USAGE</c>, so a view of one plane may use that
    /// plane's single-plane format.
    /// </summary>
    public int Format { get; }

    /// <summary>The image's <c>VkImageLayout</c> now.</summary>
    public int Layout { get; }

    /// <summary>The <c>VkAccessFlags2</c> of the image's last access.</summary>
    public ulong Access { get; }

    /// <summary>The image's timeline <c>VkSemaphore</c>.</summary>
    public ulong Semaphore { get; }

    /// <summary>The value <see cref="Semaphore"/> reaches when the image is accessible. Wait on it.</summary>
    public ulong WaitValue { get; }

    /// <summary>The value to signal <see cref="Semaphore"/> to when the submission is done with the image.</summary>
    public ulong SignalValue => WaitValue + 1;

    /// <summary>
    /// The image's queue family, or <c>VK_QUEUE_FAMILY_IGNORED</c> when it is shared concurrently
    /// across FFmpeg's families and needs no ownership transfer.
    /// </summary>
    public uint QueueFamily { get; }

    /// <summary>
    /// Records that a submission waiting on <see cref="WaitValue"/> and signalling
    /// <see cref="SignalValue"/> was made, and the layout and access it leaves the image in, so
    /// FFmpeg's next use of the image waits for it and starts from there.
    /// </summary>
    /// <param name="layout">The <c>VkImageLayout</c> the submission leaves.</param>
    /// <param name="access">The <c>VkAccessFlags2</c> of the submission's last access to the image.</param>
    /// <exception cref="InvalidOperationException">Already committed, or the lock is disposed.</exception>
    public void Commit(int layout, ulong access)
    {
        if (_handle is null)
            throw new InvalidOperationException("The lock is disposed.");
        if (_committed)
            throw new InvalidOperationException("One lock covers one submission, and this one is already committed.");

        _frame->sem_value[0] = SignalValue;
        _frame->layout[0] = layout;
        _frame->access[0] = access;
        _committed = true;
    }

    /// <summary>Unlocks the frame and drops the lock's reference to it. A second call does nothing.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null)
            return;

        if (_vulkanFrames->unlock_frame != null)
            _vulkanFrames->unlock_frame(_framesCtx, _frame);
        handle.DangerousRelease();
    }
}
