# Chiikawa Trio & Wireless Battery Monitor (Windows 10/11)

Ứng dụng siêu nhẹ (~19 MB RAM lúc chạy nền) hiển thị bộ ba **Hachiware – Chiikawa – Usagi** đi dạo trên thanh Taskbar Windows, xem nhanh phần trăm pin các thiết bị đang kết nối và nhắc nhở nghỉ ngơi vui vẻ.

> **Lưu ý:** Đây là dự án fan-made cá nhân, **phi thương mại & miễn phí**, chỉ dùng giao lưu bạn bè. Nhân vật Chiikawa / Usagi / Hachiware thuộc bản quyền tác giả Nagano & Spiralcute.

## Tính năng chính
- **Trio trên Taskbar:** Bộ ba **Hachiware – Chiikawa – Usagi** (Chiikawa ở giữa) đi bộ mượt mà trên mép trên thanh Taskbar, tự động ẩn khi bạn mở game/video Fullscreen.
- **Xem % Pin các thiết bị không dây:**
  - Chuột / bàn phím không dây 2.4GHz (hỗ trợ receiver Compx / VXE / Yizhita...)
  - Thiết bị Bluetooth Classic (SetupAPI `DEVPKEY_Bluetooth_Battery`) & Bluetooth LE GATT (`0x180F`)
  - Pin laptop (AC/Battery)
- **Nhắc nhở sức khỏe vui vẻ:**
  - **20 phút:** Chiikawa nhắc nghỉ mắt xíu
  - **40 phút:** Hachiware nhắc uống nước & ngồi thẳng lưng
  - **60 phút:** Usagi giục đứng dậy vươn vai giãn cơ
  - Tự reset bộ đếm nếu bạn rời máy >= 3 phút hoặc khóa màn hình (`Win + L`).
- **Siêu nhẹ & Tự khởi động:** Chỉ tốn ~19 MB Private RAM, tự động bật cùng Windows (có thể bật/tắt ngay trong popup hoặc menu chuột phải).

## Cách cài & dùng cho bạn bè (Windows 10/11 64-bit)
1. Tải file `UsagiBatteryMonitor-win-x64.zip` trong mục **Releases** (bên phải).
2. Giải nén ra một thư mục cố định (ví dụ `D:\Tools\UsagiBatteryMonitor`).
3. Mở **`UsagiBatteryMonitor.exe`** — không cần cài thêm `.NET Runtime`!
4. Bấm chuột trái vào 3 bé trên Taskbar để xem bảng % pin, hoặc chuột phải để bật/tắt tự khởi động và test thử câu nhắc.
5. Nếu Windows Defender SmartScreen hiện cảnh báo *"Windows protected your PC"* (do file tự build chưa ký chứng chỉ số), bấm **More info → Run anyway**.

## Build từ mã nguồn (.NET 9 SDK)
```powershell
dotnet publish UsagiBatteryMonitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ReleasePkg
```
