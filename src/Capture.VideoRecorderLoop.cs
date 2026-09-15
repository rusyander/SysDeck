// SysDeck — «Захват»: VideoRecorder — цикл кадров, отрисовка, звук и поток записи.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using WinCap = Windows.Graphics.Capture;
using WinDx = Windows.Graphics.DirectX;
using WinD3D = Windows.Graphics.DirectX.Direct3D11;

namespace SysDeck.Capture
{
    internal sealed partial class VideoRecorder : IDisposable
    {
        private string Loop()
        {
            int fps = _o.Fps;
            long nextIndex = 0;
            long maxFrames = _o.MaxDuration > TimeSpan.Zero ? _o.MaxDuration.Ticks * fps / 10000000L : long.MaxValue;
            long lastCheck = Stopwatch.GetTimestamp(), lastAudio = 0;
            long statQpc = lastCheck, statFrames = 0, statUnique = 0;
            while (!_stopRequested)
            {
                _source.Poll(_onFrame);
                if (_source.Lost) { CapLog.Write("video: source lost: " + _source.LostReason); return "device-lost"; }
                if (_writerHr < 0) return IsDeviceLost(_writerHr) ? "device-lost" : "error: " + _writerError;

                long now = Stopwatch.GetTimestamp();
                bool paused;
                long pausedTotal;
                lock (_pauseGate) { paused = _paused; pausedTotal = _pausedTotal100; }

                if (now - lastAudio >= Freq / 100)
                {
                    PumpAudio();
                    lastAudio = now;
                }

                if (!paused)
                {
                    long elapsed = To100(now - _startQpc) - pausedTotal;
                    long target = elapsed * fps / 10000000L;
                    if (target >= nextIndex)
                    {
                        TakePending(now);
                        if (target - nextIndex > fps)
                        {
                            // Отстали больше чем на секунду (система висела): пропуск вместо лавины повторов.
                            Interlocked.Add(ref _dropped, target - nextIndex);
                            nextIndex = target;
                        }
                        for (; nextIndex <= target; nextIndex++)
                        {
                            if (nextIndex >= maxFrames) return "limit-duration";
                            EmitVideo(nextIndex);
                        }
                    }
                }

                if (now - lastCheck >= Freq / 2)
                {
                    double dt = (now - statQpc) / (double)Freq;
                    if (dt >= 1.0)
                    {
                        long f = Interlocked.Read(ref _framesWritten), u = Interlocked.Read(ref _uniqueFrames);
                        _actualFps = (f - statFrames) / dt;
                        _capturedFps = (u - statUnique) / dt;
                        statFrames = f; statUnique = u; statQpc = now;
                    }
                    lastCheck = now;
                    string reason = CheckLimits();
                    if (reason != null) return reason;
                }

                // Сон до следующего кадра; источник опрашивается примерно раз в миллисекунду.
                long wait100 = paused ? 50000 : (nextIndex * 10000000L / fps) - (To100(Stopwatch.GetTimestamp() - _startQpc) - pausedTotal);
                if (wait100 > 15000) Thread.Sleep(1);
                else if (wait100 > 0) Thread.Sleep(0);
            }
            // Хвост звука до остановки.
            PumpAudio();
            return null;
        }

        private string CheckLimits()
        {
            try
            {
                FileInfo fi = new FileInfo(_o.Path);
                if (fi.Exists) Interlocked.Exchange(ref _bytes, fi.Length);
            }
            catch (IOException) { }
            if (_o.MaxBytes > 0 && Interlocked.Read(ref _bytes) >= _o.MaxBytes) return "limit-size";
            if (_o.MinFreeBytes > 0)
            {
                ulong free, total, totalFree;
                string dir = Path.GetDirectoryName(Path.GetFullPath(_o.Path));
                if (VidNative.GetDiskFreeSpaceEx(dir, out free, out total, out totalFree) && (long)Math.Min(free, (ulong)long.MaxValue) < _o.MinFreeBytes) return "low-disk";
            }
            int removed = _dev.RemovedReason();
            if (removed != 0) { CapLog.Write("video: device removed " + VidCom.Hex(removed)); return "device-lost"; }
            if (_o.MaxDuration > TimeSpan.Zero && Elapsed >= _o.MaxDuration) return "limit-duration";
            return null;
        }

        // Новый кадр источника → текстура «ожидающая» (ещё не отдана кодировщику, её можно перезаписывать).
        private void OnFrame(IntPtr tex, int w, int h, uint fmt)
        {
            VidSlot target = _pending ?? AcquireSlot();
            if (target == null) { Interlocked.Increment(ref _noSlot); return; }
            Render(target, tex, w, h, fmt);
            _pending = target;
            _pendingSynthetic = false;
            Interlocked.Increment(ref _uniqueFrames);
        }

        private void Render(VidSlot slot, IntPtr tex, int w, int h, uint fmt)
        {
            if (_windowMode)
            {
                double s = Math.Min(1.0, Math.Min((double)_outW / w, (double)_outH / h));
                int dw = Math.Max(1, Math.Min(_outW, (int)Math.Round(w * s))), dh = Math.Max(1, Math.Min(_outH, (int)Math.Round(h * s)));
                Rectangle dst = new Rectangle((_outW - dw) / 2, (_outH - dh) / 2, dw, dh);
                bool margins = dw != _outW || dh != _outH;
                if (dw == w && dh == h && fmt == 87)
                {
                    if (margins) _dev.Clear(slot.Rtv);
                    _dev.CopyRegion(slot.Texture, dst.X, dst.Y, tex, 0, 0, w, h);
                }
                else Blit(slot, tex, fmt, new Rectangle(0, 0, w, h), dst, margins);
                return;
            }
            Rectangle c = _crop;
            Rectangle avail = Rectangle.Intersect(c, new Rectangle(0, 0, w, h));
            if (c.Width == _outW && c.Height == _outH && fmt == 87)
            {
                if (avail != c) _dev.Clear(slot.Rtv);
                _dev.CopyRegion(slot.Texture, avail.X - c.X, avail.Y - c.Y, tex, avail.X, avail.Y, avail.Width, avail.Height);
            }
            else Blit(slot, tex, fmt, avail, new Rectangle(0, 0, _outW, _outH), avail != c);
            if (_cursorDraw)
                VidCursor.Draw(slot.Texture, _output.Bounds.X + c.X, _output.Bounds.Y + c.Y, (double)_outW / c.Width, 0, 0);
        }

        private void Blit(VidSlot slot, IntPtr tex, uint fmt, Rectangle src, Rectangle dst, bool clear)
        {
            if (src.Width <= 0 || src.Height <= 0) { _dev.Clear(slot.Rtv); return; }
            if (_blit == null)
            {
                if (fmt != 87) throw new VideoException("Captured pixel format " + fmt + " needs the GPU converter, which is unavailable", unchecked((int)0x887A0004));
                _dev.Clear(slot.Rtv);
                _dev.CopyRegion(slot.Texture, dst.X, dst.Y, tex, src.X, src.Y, Math.Min(src.Width, dst.Width), Math.Min(src.Height, dst.Height));
                return;
            }
            VidCom.TexDesc d = VidDevice.Desc(tex);
            IntPtr srcTex = tex;
            int tw = (int)d.Width, th = (int)d.Height;
            RectangleF r = src;
            if ((d.BindFlags & 8) == 0 || d.SampleCount > 1)
            {
                // Текстура источника без SHADER_RESOURCE: сперва копия нужного прямоугольника в свою текстуру.
                if (_srcCopy == IntPtr.Zero || _srcCopyW != src.Width || _srcCopyH != src.Height || _srcCopyFmt != d.Format)
                {
                    VidCom.Rel(ref _srcCopy);
                    _srcCopy = _dev.CreateTexture(src.Width, src.Height, d.Format, 8, 0, 0, 0);
                    _srcCopyW = src.Width; _srcCopyH = src.Height; _srcCopyFmt = d.Format;
                }
                _dev.CopyRegion(_srcCopy, 0, 0, tex, src.X, src.Y, src.Width, src.Height);
                srcTex = _srcCopy; tw = src.Width; th = src.Height;
                r = new RectangleF(0, 0, src.Width, src.Height);
            }
            _blit.Draw(srcTex, tw, th, r, slot.Rtv, dst, clear, fmt == 10);
        }

        // На тике кадра ожидающая текстура становится «последней»; в режиме CPU её содержимое уходит в память.
        private void TakePending(long now)
        {
            if (_pending == null) return;
            VidSlot p = _pending;
            bool synthetic = _pendingSynthetic;
            _pending = null;
            _pendingSynthetic = false;
            if (!_plan.GpuInput)
            {
                IntPtr buf = Readback(p.Texture);
                VidCom.Rel(ref _lastBuffer);
                _lastBuffer = buf;
            }
            _last = p;
            if (!_blackDone && !synthetic)
            {
                if (now - _startQpc > Freq)
                {
                    _blackDone = true;
                }
                else if (_blackChecks == 0 || now - _lastBlackCheckQpc >= Freq / 5)
                {
                    _lastBlackCheckQpc = now;
                    _blackChecks++;
                    if (!IsDark(p.Texture)) _blackAllDark = false;
                    _black = _blackAllDark;
                }
            }
            else if (!_blackDone && now - _startQpc > Freq) _blackDone = true;
        }

        private IntPtr Readback(IntPtr tex)
        {
            _dev.Copy(_staging, tex);
            VidCom.Mapped m;
            VidCom.Check(_dev.Map(_staging, out m), "Map(staging)");
            IntPtr buf = IntPtr.Zero;
            try
            {
                int size = _outW * _outH * 4;
                VidCom.Check(VidNative.MFCreateMemoryBuffer((uint)size, out buf), "MFCreateMemoryBuffer");
                IntPtr p; uint max, cur;
                VidCom.Check(VidCom.Fn<VidCom.DLock>(buf, 3)(buf, out p, out max, out cur), "IMFMediaBuffer.Lock");
                int row = _outW * 4;
                if (m.RowPitch == row) VidNative.CopyMemory(p, m.Data, new UIntPtr((uint)size));
                else
                    for (int y = 0; y < _outH; y++)
                        VidNative.CopyMemory(new IntPtr(p.ToInt64() + (long)y * row), new IntPtr(m.Data.ToInt64() + (long)y * m.RowPitch), new UIntPtr((uint)row));
                VidCom.Fn<VidCom.DHr>(buf, 4)(buf);
                VidCom.Fn<VidCom.DUInt>(buf, 6)(buf, (uint)size);
                IntPtr r = buf;
                buf = IntPtr.Zero;
                return r;
            }
            finally
            {
                _dev.Unmap(_staging);
                VidCom.Rel(ref buf);
            }
        }

        // Пять участков 32x32 (центр и четверти): все почти чёрные → вероятно, эксклюзивный полноэкранный режим или DRM.
        private bool IsDark(IntPtr tex)
        {
            int p = Math.Min(32, Math.Min(_outW, _outH));
            double[,] at = { { 0.5, 0.5 }, { 0.25, 0.25 }, { 0.75, 0.25 }, { 0.25, 0.75 }, { 0.75, 0.75 } };
            for (int k = 0; k < 5; k++)
            {
                int x = Math.Max(0, Math.Min(_outW - p, (int)(_outW * at[k, 0]) - p / 2));
                int y = Math.Max(0, Math.Min(_outH - p, (int)(_outH * at[k, 1]) - p / 2));
                _dev.CopyRegion(_blackStaging, k * 32, 0, tex, x, y, p, p);
            }
            VidCom.Mapped m;
            if (_dev.Map(_blackStaging, out m) < 0) return false;
            try
            {
                byte[] row = new byte[32 * 5 * 4];
                int max = 0;
                for (int y = 0; y < p; y++)
                {
                    Marshal.Copy(new IntPtr(m.Data.ToInt64() + (long)y * m.RowPitch), row, 0, row.Length);
                    for (int k = 0; k < 5; k++)
                        for (int x = 0; x < p; x++)
                        {
                            int o = (k * 32 + x) * 4;
                            int v = Math.Max(row[o], Math.Max(row[o + 1], row[o + 2]));
                            if (v > max) max = v;
                        }
                }
                return max < 24;
            }
            finally { _dev.Unmap(_blackStaging); }
        }

        private void EmitVideo(long index)
        {
            if (_last == null) return;
            lock (_qGate)
            {
                if (_queuedVideo > _o.Fps)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }
            }
            IntPtr sample;
            int hr;
            if (_plan.GpuInput) sample = VidSampleTracker.CreateSample(_last, out hr);
            else
            {
                hr = VidNative.MFCreateSample(out sample);
                if (hr >= 0) hr = VidCom.Fn<VidCom.DPtr>(sample, 42)(sample, _lastBuffer);
            }
            if (sample == IntPtr.Zero || hr < 0)
            {
                VidCom.Rel(ref sample);
                throw new VideoException("Video sample creation failed, HRESULT " + VidCom.Hex(hr), hr);
            }
            long ts = index * 10000000L / _o.Fps;
            long next = (index + 1) * 10000000L / _o.Fps;
            VidCom.Fn<VidCom.DLong>(sample, 36)(sample, ts);
            VidCom.Fn<VidCom.DLong>(sample, 38)(sample, next - ts);
            Enqueue(_writer.VideoStream, sample, true);
            Interlocked.Increment(ref _framesWritten);
        }

        private void PumpAudio()
        {
            for (int t = 0; t < _audio.Count; t++)
            {
                if (_audioBroken[t]) continue;
                IAudioSource src = _audio[t];
                VidAudioFormat af = _audioFmt[t];
                float[] fb = _audioBuf[t];
                short[] pcm = _pcmBuf[t];
                for (int guard = 0; guard < 64; guard++)
                {
                    int n;
                    long ts;
                    try { n = src.Read(fb, fb.Length / af.Channels, out ts); }
                    catch (Exception ex)
                    {
                        _audioBroken[t] = true;
                        CapLog.Write("video: audio track " + t + " failed, continuing without it: " + ex.Message);
                        AddNote("audio track " + t + " failed");
                        break;
                    }
                    if (n <= 0) break;
                    long mapped;
                    if (!MapAudioTime(ts, out mapped)) continue;
                    int count = n * af.Channels;
                    for (int i = 0; i < count; i++)
                    {
                        float v = fb[i];
                        pcm[i] = v >= 1f ? short.MaxValue : v <= -1f ? short.MinValue : (short)(v * 32767f);
                    }
                    IntPtr buf = IntPtr.Zero, sample = IntPtr.Zero;
                    try
                    {
                        VidCom.Check(VidNative.MFCreateMemoryBuffer((uint)(count * 2), out buf), "MFCreateMemoryBuffer(audio)");
                        IntPtr p; uint max, cur;
                        VidCom.Check(VidCom.Fn<VidCom.DLock>(buf, 3)(buf, out p, out max, out cur), "IMFMediaBuffer.Lock(audio)");
                        Marshal.Copy(pcm, 0, p, count);
                        VidCom.Fn<VidCom.DHr>(buf, 4)(buf);
                        VidCom.Fn<VidCom.DUInt>(buf, 6)(buf, (uint)(count * 2));
                        VidCom.Check(VidNative.MFCreateSample(out sample), "MFCreateSample(audio)");
                        VidCom.Fn<VidCom.DPtr>(sample, 42)(sample, buf);
                        VidCom.Fn<VidCom.DLong>(sample, 36)(sample, mapped);
                        VidCom.Fn<VidCom.DLong>(sample, 38)(sample, n * 10000000L / af.Rate);
                        Enqueue(_writer.AudioStreams[t], sample, false);
                        sample = IntPtr.Zero;
                    }
                    finally
                    {
                        VidCom.Rel(ref sample);
                        VidCom.Rel(ref buf);
                    }
                }
            }
        }

        // Время звука без пауз; кусок, начавшийся во время паузы, выбрасывается.
        private bool MapAudioTime(long ts, out long mapped)
        {
            mapped = 0;
            lock (_pauseGate)
            {
                long sub = 0;
                foreach (long[] iv in _pauses)
                {
                    if (ts >= iv[0] && ts < iv[1]) return false;
                    if (ts >= iv[1]) sub += iv[1] - iv[0];
                }
                if (_paused && ts >= To100(_pauseStartQpc - _startQpc)) return false;
                mapped = ts - sub;
            }
            return mapped >= 0;
        }

        private void Enqueue(uint stream, IntPtr sample, bool video)
        {
            QItem it = new QItem();
            it.Stream = stream; it.Sample = sample; it.Video = video;
            lock (_qGate)
            {
                _queue.Enqueue(it);
                if (video) _queuedVideo++;
                Monitor.Pulse(_qGate);
            }
        }

        // ---- поток писателя: WriteSample по очереди и Finalize ----
        private void WriterMain()
        {
            try
            {
                while (true)
                {
                    QItem it;
                    lock (_qGate)
                    {
                        while (_queue.Count == 0 && !_writerStop) Monitor.Wait(_qGate);
                        if (_queue.Count == 0) break;
                        it = _queue.Dequeue();
                        if (it.Video) _queuedVideo--;
                    }
                    if (_writerHr >= 0)
                    {
                        int hr = _writer.Write(it.Stream, it.Sample);
                        if (hr < 0)
                        {
                            _writerError = "WriteSample(" + (it.Video ? "video" : "audio") + ") HRESULT " + VidCom.Hex(hr);
                            _writerHr = hr;
                            CapLog.Write("video: " + _writerError);
                        }
                    }
                    IntPtr s = it.Sample;
                    VidCom.Rel(ref s);
                }
                int fh = _writer.FinalizeWriter();
                if (fh < 0) CapLog.Write("video: Finalize HRESULT " + VidCom.Hex(fh) + " " + _o.Path);
            }
            catch (Exception ex) { CapLog.Report(ex); }
            finally { _writerDone.Set(); }
        }

        // Возвращает true, если писатель брошен (Finalize завис): тогда MF нельзя выключать.
        private bool Shutdown()
        {
            lock (_pauseGate)
            {
                if (_running) _finalElapsed100 = Math.Max(0, ElapsedLocked(Stopwatch.GetTimestamp()));
            }
            foreach (IAudioSource a in _audio)
                try { a.Stop(); } catch (Exception ex) { CapLog.Report(ex); }

            bool abandoned = false;
            if (_writerThread != null)
            {
                lock (_qGate) { _writerStop = true; Monitor.Pulse(_qGate); }
                if (!_writerDone.WaitOne(9000))
                {
                    abandoned = true;
                    _writer.Abandoned = true;
                    CapLog.Write("video: writer did not finish in 9 s, abandoned: " + _o.Path);
                }
            }
            if (_writer != null) _writer.Dispose();

            foreach (IAudioSource a in _audio)
                try { a.Dispose(); } catch (Exception ex) { CapLog.Report(ex); }
            if (_source != null) try { _source.Dispose(); } catch (Exception ex) { CapLog.Report(ex); }
            foreach (VidSlot s in _slots)
            {
                VidSampleTracker.Unregister(s);
                VidCom.Rel(ref s.Rtv);
                VidCom.Rel(ref s.Texture);
            }
            _slots.Clear();
            VidCom.Rel(ref _lastBuffer);
            VidCom.Rel(ref _staging);
            VidCom.Rel(ref _srcCopy);
            VidCom.Rel(ref _blackStaging);
            if (_blit != null) _blit.Dispose();
            if (_dev != null && !abandoned) _dev.Dispose();
            try
            {
                FileInfo fi = new FileInfo(_o.Path);
                if (fi.Exists) Interlocked.Exchange(ref _bytes, fi.Length);
            }
            catch (IOException) { }
            return abandoned;
        }
    }
}
