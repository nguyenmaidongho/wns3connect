# WNS3Connect (WebNow S3 Connect)

App Windows để quản lý S3 / S3-compatible (CloudFly `s3.cloudfly.vn`, AWS, R2, MinIO…), giao diện kiểu S3 Browser.
.NET 10 + Avalonia 12 + AWSSDK.S3 — cùng stack với WNTerm.

## Chạy

- Bản đóng gói: `publish/win-x64/WNS3Connect.exe` (1 file, không cần cài .NET).
- Từ source: `dotnet run --project src/WNS3Connect`
- Đóng gói lại:

```bash
dotnet publish src/WNS3Connect -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish/win-x64
```

## Vì sao nhanh

- Mỗi thư mục chỉ gọi `ListObjectsV2` với `Delimiter=/` (không quét cả bucket). Trang đầu (1000 mục) hiện ngay, các trang sau nối dần; đổi thư mục giữa chừng thì hủy lượt cũ.
- Danh sách đã xem được giữ trong cache: quay lại thư mục cũ hiện tức thì, quá 45 giây thì làm mới ngầm.
- Rê chuột lên thư mục ~150ms là app nạp sẵn trang đầu của thư mục đó.
- Danh sách ảo hóa (chỉ dựng các dòng đang thấy) và thêm từng lô 1000 dòng bằng 1 sự kiện.
- Xóa: thư mục được liệt kê và xóa cùng lúc (mỗi lệnh `DeleteObjects` 1000 file, 4 lệnh song song); app tự thêm `Content-MD5` mà Ceph/CloudFly bắt buộc (SDK v4 không gửi) nên không phải xóa từng file. Đo trên S3 giả: 25.000 file 183,7s → 6,1s.
- Upload/download chạy song song (mặc định 4 file, chỉnh ở Công cụ), file lớn chia multipart 16 MB × 8 luồng.

## Đọc kho restic của WebNowBackup (1.6)

Plugin WebNowBackup (DirectAdmin) lưu bản "Cloud · Incremental" thành kho **restic** đã mã hóa tại `WebNowBackup/<máy chủ>/restic/`. WNS3Connect đọc thẳng kho này — **không cần máy chủ, không cần cài restic** — để lấy file ra khi máy chủ sập.

- Mở thư mục `WebNowBackup/<máy chủ>/` hoặc `.../restic/` → thanh xanh **Mở kho restic…** (hoặc chuột phải thư mục → *Mở như kho restic…*, menu File → *Mở kho restic tại thư mục này…*).
- Nhập mật khẩu kho (WebNowBackup → Kho lưu trữ → xem mật khẩu restic). Tích *Lưu mật khẩu* thì mật khẩu được mã hóa DPAPI trong tài khoản S3 đó và **đồng bộ E2E qua tài khoản WebNow** như Secret Key (`resticRepos` trong vault) — cài lại máy vẫn còn.
- Bên trái: các snapshot (lọc theo user, database, ngày, id). Snapshot WebNowBackup hiện rõ *user · Home + cấu hình* hoặc *Database: tên_db*, mở sẵn đúng thư mục home / file `.sql`.
- Ô **Tìm** (Ctrl+F): tìm theo tên ở mọi cấp của thư mục đang xem (vd `wp-config.php`); tải kết quả về vẫn giữ cây thư mục.
- Bên phải: duyệt thư mục, **Tải về máy…** (các mục chọn), **Tải cả thư mục này…** (tạo thư mục con cùng tên), **Mở** (xem nhanh 1 file). Khôi phục chạy trong tab Tác vụ, có tiến độ / dừng; giữ giờ sửa file; symlink bỏ qua (ghi trong Nhật ký).
- Chỉ đọc: không tạo lock, không ghi gì lên kho → mở song song khi máy chủ đang sao lưu vẫn an toàn.
- Hỗ trợ kho restic bản 1 và 2 (nén zstd), mọi tham số scrypt. Kiểm MAC Poly1305 + SHA-256 từng blob: dữ liệu hỏng chỉ làm lỗi đúng file đó.
- Nhanh: khôi phục gom blob theo pack (1 request cho nhiều file nhỏ), 8 luồng; index + snapshot (vẫn mã hóa) được cache ở `%LOCALAPPDATA%\WNS3Connect
estic-cache\<id kho>` nên lần mở sau gần như tức thì. Đo: 2.400 file / 35 MB khôi phục ~8 s (restic 0.18.1 trên cùng máy: 22 s).
- Công cụ → *Xóa cache kho restic* (cache chỉ chứa dữ liệu đã mã hóa, xóa thoải mái).
- Code: `Services/Restic/` (`ResticCrypto` = scrypt / AES-256-CTR / Poly1305-AES tự viết, `ResticRepository` = key, config, index, snapshot, tree, restore), cửa sổ `Views/ResticWindow.axaml`.

## Tài khoản WebNow (dùng chung WNTerm)

- Nút **Đăng nhập WebNow** góc phải: đăng nhập bằng email/mật khẩu WNTerm, hoặc dùng luôn phiên đăng nhập của WNTerm trên máy này.
- Danh sách tài khoản S3 (kèm Secret Key) được mã hóa đầu-cuối bằng cùng khóa vault, lưu ở **ngăn riêng** trên server: `GET/PUT /api/v1/appvault?app=wns3connect` (bảng `app_vaults` / `app_vault_blobs`, giữ 30 phiên bản). WNTerm và WNS3Connect không bao giờ ghi vào dữ liệu của nhau.
- Thêm/sửa/xóa tài khoản S3 thì app tự backup; cài lại máy chỉ cần đăng nhập là có lại.
- Bản 1.0/1.1 từng ghi vào vault chung của WNTerm; lần đồng bộ đầu tiên của 1.2 tự chuyển dữ liệu đó sang ngăn riêng (chỉ đọc, không ghi lại vault WNTerm).
- Cần server có `/appvault` (code ở `D:\Software\WNTerm\snterm\server`: `src/AppVault.php`, route trong `src/Api.php`, bảng trong `schema.sql` → chạy `php tools/migrate.php`).

## Dữ liệu

- `%APPDATA%\WNS3Connect\accounts.json` — tài khoản S3, Secret Key mã hóa DPAPI (chỉ user Windows này giải mã được).
- `%APPDATA%\WNS3Connect\settings.json` — cài đặt + phiên đăng nhập WebNow (token/khóa vault mã hóa DPAPI).
- Biến môi trường để test: `WNS3_DATA_DIR`, `WNS3_LOCAL_DIR`, `WNS3_ACCOUNT_API`, `WNS3_AUTOPATH=bucket/thu-muc/`, `WNTERM_DATA_DIR`.

## Test

- `tests/FakeS3` — S3 giả (path-style, có 25.000 file trong 1 thư mục, độ trễ giả lập `FAKES3_LATENCY`) + API tài khoản giả `/api/v1`.

```bash
dotnet run -c Release --project tests/FakeS3 -- --urls http://127.0.0.1:9900
```

- `tests/SmokeTest` — 17 kiểm tra: list/phân trang/cache/prefetch, upload multipart, download, dừng, xóa đệ quy, presign.
- `tests/ResticTest` — vector chuẩn (RFC 8439 Poly1305, RFC 7914 scrypt, NIST AES-CTR) + đọc kho do **restic 0.18.1 thật** tạo ra (bản 1 và 2), so từng byte và giờ sửa với nguồn, qua thư mục và qua FakeS3; kho hỏng; mật khẩu trong vault.

```bash
dotnet run -c Release --project tests/ResticTest -- <kho> <mật khẩu> <thư mục nguồn> [s3]
dotnet run -c Release --project tests/ResticTest -- full <kho> <mật khẩu> <thư mục đã backup> <thư mục ra> [s3]
dotnet run -c Release --project tests/ResticTest -- corrupt <kho> <mật khẩu>
```

- `tests/VaultInterop` — 16 kiểm tra dùng chính `WNTerm.Core` (D:\Software\WNTerm) và WNS3Connect cùng đọc/ghi 1 vault.
