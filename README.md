# Video Processing System

Hệ thống mẫu xử lý video bất đồng bộ, xây dựng bằng .NET 10 và Docker Compose. API nhận video, lưu file gốc vào MinIO, lưu metadata vào MongoDB và gửi message đến RabbitMQ. Worker đọc message, cập nhật trạng thái và giả lập xử lý video trong 5 giây. Giao diện Blazor Server cho phép upload và theo dõi trạng thái.

## Kiến trúc

```text
Browser
  |
  v
Web.Client (Blazor Server) ---> Video.API (Minimal API)
                                      |       |       |
                                      v       v       v
                                   MinIO  MongoDB  RabbitMQ
                                                     |
                                                     v
                                                Video.Worker
                                                  |       |
                                                  v       v
                                                MinIO  MongoDB
```

- **Video.API**: `POST /api/videos/upload` nhận multipart form field `file`; upload file vào bucket `videos`, tạo metadata trạng thái `Pending` rồi publish `VideoUploadedMessage`. `GET /api/videos` trả metadata, video mới nhất trước.
- **Video.Worker**: consume queue `video-uploaded`, chuyển trạng thái `Processing`, tải file từ MinIO, chờ 5 giây để giả lập xử lý, cập nhật `Completed` hoặc `Failed`, rồi ack/nack message.
- **MongoDB**: lưu `VideoId`, tên file, trạng thái và thời điểm upload.
- **MinIO**: lưu file video gốc.
- **RabbitMQ**: chuyển message upload từ API đến Worker.
- **Web.Client**: Blazor Server gọi API để upload và làm mới danh sách video mỗi 3 giây.

## Yêu cầu

- Hệ điều hành Windows 10/11 hoặc Linux/macOS hỗ trợ Docker Desktop/Engine.
- `.NET SDK 10.0` để build và chạy source ngoài Docker.
- Docker Desktop hoặc Docker Engine với Docker Compose v2.
- Kết nối Internet để pull các image lần đầu.

## Cấu trúc source

```text
src/
├── Video.Contracts/       # Record dùng chung qua RabbitMQ
├── Video.API/             # ASP.NET Core Minimal API
├── Video.Worker/          # Worker Service xử lý bất đồng bộ
└── Web.Client/            # Blazor Server
```

## Chạy bằng Docker Compose

Mở PowerShell tại thư mục gốc repository:

```powershell
docker compose config
docker compose build
docker compose up -d
docker compose ps
```

Compose khởi động MongoDB, MinIO, RabbitMQ, Video.API, Video.Worker và Web.Client trên network `video_network`. Các ứng dụng liên lạc qua service name trong Docker; không dùng `localhost` cho kết nối nội bộ.

Mở giao diện tại **http://localhost:8080**. Các địa chỉ dịch vụ khác:

| Dịch vụ | URL/Port từ máy host |
| --- | --- |
| Web.Client | http://localhost:8080 |
| Video.API | http://localhost:8081 |
| Danh sách video | http://localhost:8081/api/videos |
| MongoDB | `localhost:27017` |
| MinIO API | http://localhost:9000 |
| MinIO Console | http://localhost:9001 |
| RabbitMQ AMQP | `localhost:5672` |
| RabbitMQ Management | http://localhost:15672 |

Thông tin đăng nhập mặc định phục vụ local development:

- MinIO: `minioadmin` / `minioadmin`
- RabbitMQ: `videoapp` / `videoapp_password`

Có thể ghi đè các giá trị trên bằng biến môi trường hoặc file `.env` ở thư mục gốc. Không dùng các mật khẩu mặc định này cho môi trường production. `.env` đã được thêm vào `.gitignore`.

## Build và chạy riêng từng project

Từ thư mục gốc:

```powershell
dotnet build src/Video.API/Video.API.csproj
dotnet build src/Video.Worker/Video.Worker.csproj
dotnet build src/Web.Client/Web.Client.csproj
```

Khi chạy ngoài Docker, các `appsettings.json` mặc định dùng `localhost`; cần bảo đảm MongoDB, MinIO và RabbitMQ đang chạy trên các port mặc định tương ứng.

## Logs và dừng hệ thống

```powershell
docker compose logs -f
docker compose logs -f video-api video-worker
docker compose down
```

`docker compose down` giữ lại dữ liệu trong các named volume. Chỉ dùng `docker compose down -v` khi chủ ý xóa dữ liệu MongoDB, MinIO và RabbitMQ.

## Ghi chú MinIO

Compose hiện pin digest `bitnamilegacy/minio@sha256:451fe6858cb770cc9d0e77ba811ce287420f781c7c1b806a386f6896471a349c` (bản `2025.5.24`) vì registry image MinIO chính thức không cho phép pull trong môi trường triển khai này. Đây là image legacy đã ngừng cập nhật, chỉ dùng cho bài tập/local development; không dùng cho production. Với triển khai lâu dài, hãy thay bằng image MinIO/AiStor được tổ chức của bạn cấp quyền truy cập và duy trì.

## Kiểm tra sau khi khởi động

```powershell
docker compose ps
docker compose logs --tail=100 video-api video-worker web-client
Invoke-RestMethod http://localhost:8081/api/videos
```