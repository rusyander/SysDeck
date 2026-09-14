@echo off
rem ==== Windows Process Cleaner - media test fixtures (stage 6: HLS, DASH, TS, WebM, subtitles) ====
rem Regenerates the small streams under tests\media\. Needs ffmpeg ONLY to regenerate: the files are committed and the
rem test suite never runs ffmpeg (ffprobe is an optional oracle via WPC_FFPROBE). Usage: make-fixtures.bat [path\ffmpeg.exe]
setlocal
chcp 65001 >nul
set FF=%~1
if "%FF%"=="" set FF=ffmpeg
set D=%~dp0
set Q=-hide_banner -loglevel error -y
set V=-f lavfi -i testsrc2=size=160x90:rate=10
set A=-f lavfi -i sine=frequency=440:sample_rate=48000
set X264=-c:v libx264 -preset veryfast -g 10 -keyint_min 10 -sc_threshold 0 -b:v 60k
set AAC=-c:a aac -b:a 48k
cd /d "%D%"

rem --- HLS, MPEG-TS, video+audio muxed, 4 x 1 s
if not exist hls-ts mkdir hls-ts
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -f hls -hls_time 1 -hls_playlist_type vod -hls_segment_filename hls-ts\seg%%d.ts hls-ts\index.m3u8 || exit /b 1

rem --- HLS, MPEG-TS, AES-128 (key 000102..0f, IV 0x0f0e..00 explicit in the playlist)
if not exist hls-aes mkdir hls-aes
powershell -NoProfile -Command "[IO.File]::WriteAllBytes('%D%hls-aes\key.bin', [byte[]](0..15))" || exit /b 1
> hls-aes\keyinfo.txt echo key.bin
>> hls-aes\keyinfo.txt echo %D%hls-aes\key.bin
>> hls-aes\keyinfo.txt echo 0f0e0d0c0b0a09080706050403020100
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -f hls -hls_time 1 -hls_playlist_type vod -hls_key_info_file hls-aes\keyinfo.txt -hls_segment_filename hls-aes\seg%%d.ts hls-aes\index.m3u8 || exit /b 1
del hls-aes\keyinfo.txt

rem --- HLS, fMP4 (EXT-X-MAP init + .m4s)
if not exist hls-fmp4 mkdir hls-fmp4
pushd hls-fmp4
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -f hls -hls_time 1 -hls_playlist_type vod -hls_segment_type fmp4 -hls_fmp4_init_filename init.mp4 -hls_segment_filename seg%%d.m4s index.m3u8 || (popd & exit /b 1)
popd

rem --- HLS, one TS file addressed by EXT-X-BYTERANGE
if not exist hls-byterange mkdir hls-byterange
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -f hls -hls_time 1 -hls_playlist_type vod -hls_flags single_file hls-byterange\index.m3u8 || exit /b 1

rem --- HLS master: 2 video variants (160x90, 320x180) + separate audio rendition group
if not exist hls-master mkdir hls-master
"%FF%" %Q% %V% %A% -t 4 -filter_complex "[0:v]split=2[v1][v2];[v2]scale=320:180[v2o]" -map "[v1]" -map "[v2o]" -map 1:a %X264% -b:v:1 120k %AAC% -f hls -hls_time 1 -hls_playlist_type vod -master_pl_name master.m3u8 -var_stream_map "v:0,agroup:aud v:1,agroup:aud a:0,agroup:aud,language:en,name:English,default:yes" -hls_segment_filename hls-master\v%%v_seg%%d.ts hls-master\v%%v.m3u8 || exit /b 1

rem --- WebVTT subtitles (standalone, and as HLS segments)
> subs.vtt echo WEBVTT
>> subs.vtt echo.
>> subs.vtt echo 00:00:00.500 --^> 00:00:01.500
>> subs.vtt echo Привет
>> subs.vtt echo.
>> subs.vtt echo 00:00:02.000 --^> 00:00:03.000
>> subs.vtt echo Hello
if not exist hls-subs mkdir hls-subs
"%FF%" %Q% -i subs.vtt -c:s webvtt -f segment -segment_time 2 -segment_list hls-subs\subs.m3u8 -segment_list_type m3u8 -segment_format webvtt hls-subs\sub%%d.vtt || exit /b 1

rem --- DASH: SegmentTemplate + SegmentTimeline (separate video and audio adaptation sets)
if not exist dash-tl mkdir dash-tl
pushd dash-tl
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -map 0:v -map 1:a -f dash -seg_duration 1 -use_template 1 -use_timeline 1 -init_seg_name init$RepresentationID$.m4s -media_seg_name seg$RepresentationID$-$Number$.m4s manifest.mpd || (popd & exit /b 1)
popd

rem --- DASH: SegmentTemplate with $Number$ and duration (no timeline)
if not exist dash-num mkdir dash-num
pushd dash-num
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -map 0:v -map 1:a -f dash -seg_duration 1 -use_template 1 -use_timeline 0 -init_seg_name init$RepresentationID$.m4s -media_seg_name seg$RepresentationID$-$Number$.m4s manifest.mpd || (popd & exit /b 1)
popd

rem --- DASH: one file per representation (SegmentBase / indexRange)
if not exist dash-single mkdir dash-single
pushd dash-single
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -map 0:v -map 1:a -f dash -seg_duration 1 -single_file 1 -single_file_name rep$RepresentationID$.mp4 manifest.mpd || (popd & exit /b 1)
popd

rem --- yt-dlp-shaped separate tracks: MP4-muxable pair and WebM pair
"%FF%" %Q% %V% -t 4 %X264% -an -movflags +faststart track-avc.mp4 || exit /b 1
"%FF%" %Q% %A% -t 4 %AAC% -vn track-aac.m4a || exit /b 1
"%FF%" %Q% %V% -t 4 -c:v libvpx-vp9 -deadline realtime -b:v 60k -g 10 -an track-vp9.webm || exit /b 1
"%FF%" %Q% %A% -t 4 -c:a libopus -b:a 32k -vn track-opus.webm || exit /b 1

rem --- plain progressive file and an MPEG-TS with PTS near the 33-bit wrap
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -movflags +faststart plain.mp4 || exit /b 1
"%FF%" %Q% %V% %A% -t 4 %X264% %AAC% -output_ts_offset 95443 -f mpegts wrap.ts || exit /b 1
echo fixtures OK
