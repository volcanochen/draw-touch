#!/usr/bin/env bash
set -e
export JAVA_HOME="C:/Users/admin/.workbuddy/binaries/jdk/jdk-17.0.20.1+1"
export ANDROID_HOME="C:/Users/admin/.workbuddy/binaries/android-sdk"
export ANDROID_SDK_ROOT="$ANDROID_HOME"
export GRADLE_USER_HOME="C:/Users/admin/.gradle"
GRADLE="C:/Users/admin/.gradle/wrapper/dists/gradle-8.9-all/6m0mbzute7p0zdleavqlib88a/gradle-8.9/bin/gradle.bat"

cd "C:/Users/admin/WorkBuddy/2026-10-26-22-33-57/drawing-phone/PhoneTablet"
"$GRADLE" -I "C:/Users/admin/.workbuddy/binaries/gradle/mirrors.gradle" "$@"
