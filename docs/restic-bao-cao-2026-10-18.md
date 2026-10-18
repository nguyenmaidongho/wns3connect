# Đọc kho restic WebNowBackup trong WNS3Connect — báo cáo đêm 17→18/10/2026

## Đã làm (bản 1.6.0, exe mới ở `publish/win-x64/WNS3Connect.exe`)

- WNS3Connect giờ **tự mở được kho restic** WebNowBackup trên S3 (`WebNowBackup/<máy chủ>/restic/`) bằng mật khẩu kho. Không cần máy chủ DirectAdmin, không cần cài restic.
- Cách dùng: vào `WebNowBackup/<máy chủ>/` → thanh xanh **Mở kho restic…** → nhập mật khẩu → chọn snapshot (lọc theo user / database / ngày) → duyệt → **Tải về máy…** / **Tải cả thư mục này…** / **Mở**.
- Snapshot của WebNowBackup hiện rõ *user · Home + cấu hình* hoặc *Database: tên_db* và mở sẵn đúng thư mục home hoặc file `.sql`.
- Tích "Lưu mật khẩu" thì mật khẩu được lưu theo tài khoản S3 và đồng bộ mã hóa đầu-cuối qua tài khoản WebNow, giống Secret Key. Cài lại máy, đăng nhập WebNow là có lại.
- **Tìm file** (ô Tìm / Ctrl+F) theo tên ở mọi cấp của thư mục đang xem; tải kết quả về vẫn giữ cây thư mục (đã sửa lỗi 2 thư mục trùng tên bị ghi đè lên nhau, phát hiện lúc test).
- Công cụ → **Xóa cache kho restic**.
- Chỉ đọc: không tạo lock, không ghi gì lên cloud, nên mở ngay cả khi máy chủ đang sao lưu cũng an toàn.

## Đã kiểm chứng

| Kiểm tra | Kết quả |
|---|---|
| Vector chuẩn Poly1305 (RFC 8439), scrypt (RFC 7914 ×2), AES-256-CTR (NIST) | OK |
| Kho do **restic 0.18.1 thật** tạo ra, bản 2 (zstd), tag giống hệt WebNowBackup | OK: đọc snapshot, duyệt, khôi phục từng byte + giờ sửa khớp nguồn |
| Kho bản 1, 2 snapshot, 2.400 file, mật khẩu có dấu và khoảng trắng | OK cả 2 snapshot |
| Qua S3 giả có độ trễ 150 ms/request | 2.400 file / 35 MB: lập kế hoạch 1,3 s, ghi 7,5 s (restic tự khôi phục cùng kho trên máy: 22 s) |
| Sai mật khẩu, mật khẩu đã lưu bị đổi | Báo lỗi rõ ràng, cho nhập lại |
| Pack bị hỏng 1 byte | Chỉ file liên quan báo lỗi, file khác vẫn ra, không còn file tạm |
| Đường dẫn đích > 260 ký tự, tên file Windows cấm (con, nul…) | OK (tự thêm `\\?\`, đổi tên) |
| Mật khẩu kho trong vault: tài khoản cũ/mới, bản vault cũ | OK |
| SmokeTest cũ (22 kiểm tra S3) | ALL PASS — không ảnh hưởng tính năng cũ |
| Giao diện: banner, mở kho, chọn snapshot, tải thư mục, tìm + tải kết quả (bản Debug, Release và exe đóng gói) | OK (chụp màn hình) |

## Chưa kiểm chứng

- **Chưa chạy với kho thật trên CloudFly của bạn.** Theo quy ước, mình không tự kết nối vào bucket thật. Đây là việc quan trọng nhất cần làm sáng nay.
- `tests/VaultInterop` (chạy với server PHP thật) chưa chạy lại; phần mật khẩu restic trong vault mới chỉ có test riêng.

## Kế hoạch tiếp theo (xếp theo độ ưu tiên)

1. **Thử với kho thật (bạn làm, ~5 phút):** mở exe mới → bucket backup → `WebNowBackup/<máy chủ>/` → Mở kho restic → nhập key. Xem: số snapshot có khớp trang WebNowBackup không, mở 1 home, tải 1 thư mục nhỏ và 1 file `.sql`. Nếu lỗi, chép dòng trong tab **Nhật ký** gửi mình. Muốn mình tự chẩn đoán (chỉ đọc) thì cho phép mình chạy `ResticTest` với tài khoản đó.
2. **Đo với kho lớn:** lần mở đầu tiên phải tải toàn bộ index (lần sau dùng cache). Nếu kho thật mất hơn ~10 s thì thêm thanh tiến độ "Đang nạp index x/y" và nạp song song nhiều hơn.
3. **So sánh một file giữa các bản** (cùng đường dẫn ở mọi snapshot của user, kèm kích thước và giờ sửa). Tìm file trong một snapshot đã làm xong đêm nay.
4. **Gộp snapshot theo bản sao lưu**: home + các database cùng mã `b:` thành một dòng, kèm ô chọn user.
5. **Định dạng WebNowBackup khác:** "Cloud · Nén" (`tar.gz` + danh sách file) và "Cloud · Thư mục". Hiện mới đọc được kho restic.
6. **Mở kho restic nằm trên máy** (đã tải về, hoặc kho ở Google Drive/OneDrive tải xuống): phần đọc đã hỗ trợ sẵn (`LocalResticBackend`), chỉ còn thiếu menu.
7. Lặt vặt: symlink hiện đang bỏ qua (ghi vào Nhật ký), có thể ghi ra file `.symlink.txt` chứa đường dẫn đích.
