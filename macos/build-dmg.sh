#!/bin/bash
set -euo pipefail

if [[ "$(uname -s)" != Darwin ]] || ! xcodebuild -version >/dev/null 2>&1; then
    printf '%s\n' '请在安装并初始化 Xcode 的 Mac 上执行此脚本。' >&2
    exit 1
fi

macos_dir="$(cd -- "$(dirname -- "$0")" && pwd -P)"
output_dir="$macos_dir/dist"
build_dir="$(mktemp -d "${TMPDIR:-/tmp}/codex-usage-monitor.XXXXXXXX")"
mount_dir="$build_dir/mounted"
mounted=0

cleanup() {
    if [[ "$mounted" == 1 ]]; then
        hdiutil detach "$mount_dir" >/dev/null || return
    fi
    rm -rf -- "$build_dir"
}
trap cleanup EXIT

xcodebuild -project "$macos_dir/CodexUsageMonitor.xcodeproj" \
    -scheme CodexUsageMonitor -configuration Release \
    -destination 'generic/platform=macOS' \
    -derivedDataPath "$build_dir/DerivedData" \
    ARCHS='arm64 x86_64' ONLY_ACTIVE_ARCH=NO CODE_SIGN_IDENTITY=- build

app_name=CodexUsageMonitor
app="$build_dir/DerivedData/Build/Products/Release/$app_name.app"
plist="$app/Contents/Info.plist"
plutil -lint "$plist"
lipo -verify_arch arm64 x86_64 "$app/Contents/MacOS/$app_name"
lipo -archs "$app/Contents/MacOS/$app_name"
codesign --verify --strict --verbose=2 "$app"
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$plist")"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf '无法识别应用版本：%s\n' "$version" >&2
    exit 1
fi

stage="$build_dir/dmg"
mkdir -p -- "$stage" "$mount_dir"
ditto "$app" "$stage/$app_name.app"
ln -s /Applications "$stage/Applications"

filename="$app_name-$version-macOS-universal.dmg"
dmg="$build_dir/$filename"
hdiutil create -volname 'Codex Usage Monitor' -format UDZO -srcfolder "$stage" "$dmg"
hdiutil verify "$dmg"
hdiutil attach "$dmg" -readonly -nobrowse -mountpoint "$mount_dir"
mounted=1
[[ "$(readlink "$mount_dir/Applications")" == /Applications ]]
codesign --verify --strict --verbose=2 "$mount_dir/$app_name.app"
hdiutil detach "$mount_dir"
mounted=0

(cd "$build_dir" && shasum -a 256 "$filename" > "$filename.sha256")
mkdir -p -- "$output_dir"
mv -f -- "$dmg" "$build_dir/$filename.sha256" "$output_dir/"
printf 'macOS 安装包：%s/%s\n' "$output_dir" "$filename"
