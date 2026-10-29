#!/bin/sh
# 采集 sec_touchscreen 的原始触摸数据（接触面积/压力/坐标）
# 放在手机上跑：adb shell sh /data/local/tmp/capture_touch.sh 180 > /sdcard/touch.log
DEV=/dev/input/event9
SECS=${1:-180}
OUT=/sdcard/touch_$(date +%H%M%S).log
getevent -lt $DEV > "$OUT" 2>/dev/null &
GPID=$!
sleep $SECS
kill $GPID 2>/dev/null
echo "DONE $OUT"
