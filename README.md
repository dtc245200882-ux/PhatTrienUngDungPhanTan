# Hệ thống xử lý video bất đồng bộ

> Bài tập lớn Đề tài 14, xây dựng trên .NET 10, ASP.NET Core Minimal API, Blazor Server, RabbitMQ, MongoDB và MinIO.

## Tổng quan

`Video.API` nhận video và lưu file vào MinIO, metadata vào MongoDB. Client tạo một job xử lý riêng; API ghi job vào MongoDB và publish message persistent lên RabbitMQ. `Video.Worker` consume với manual acknowledgement, cập nhật tiến độ của job và video. Lỗi được retry qua queue có TTL; hết số lần retry thì message được chuyển vào DLQ.

## Yêu cầu

Compose hiện pin image `bitnamilegacy/minio` đã ngừng cập nhật, chỉ phù hợp cho bài tập/local development. Thay bằng MinIO/AiStor image được duy trì và cấp quyền sử dụng trước khi triển khai lâu dài.

## Chạy hệ thống

Tại thư mục gốc repository, tạo `.env` từ template:

```powershell
Copy-Item .env.example .env
```

Các giá trị trong `.env.example` chỉ dành cho local development. Thay credential trước khi chia sẻ môi trường; không commit `.env`.

Kiểm tra cấu hình, build image và khởi động:

```powershell
docker compose config --quiet
docker compose up -d --build
docker compose ps
```

Các cổng host mặc định (có thể đổi trong `.env`): Web.Client `8080`, Video.API `8081`, MongoDB `27017`, MinIO API/Console `9000`/`9001`, RabbitMQ AMQP/Management `5672`/`15672`. Mở giao diện tại [http://localhost:8080](http://localhost:8080), API tại [http://localhost:8081](http://localhost:8081), MinIO Console tại [http://localhost:9001](http://localhost:9001) và RabbitMQ Management tại [http://localhost:15672](http://localhost:15672). API `/health` kiểm tra MongoDB, RabbitMQ và bucket MinIO; Worker health dùng cổng nội bộ cấu hình trong `.env`.

## API

Năm endpoint nghiệp vụ hỗ trợ cả đường dẫn có và không có tiền tố `/api`:

| Phương thức | Đường dẫn | Mô tả |
| --- | --- | --- |
| `POST` | `/videos` | Multipart form field `file`; lưu file vào MinIO và metadata vào MongoDB. |
| `POST` | `/videos/{id}/jobs` | Tạo job `Thumbnail`, `Metadata` hoặc `Transcode` (JSON `{ "jobType": "Metadata" }`), lưu job và gửi message đến RabbitMQ. |
| `GET` | `/videos` | Liệt kê video mới nhất trước; hỗ trợ `?status=Completed` (các trạng thái `Uploaded`, `Pending`, `Processing`, `Completed`, `Failed`). |
| `GET` | `/videos/{id}` | Chi tiết video và danh sách job liên quan. |
| `GET` | `/jobs/{id}` | Trạng thái, kết quả, lỗi và Correlation ID của job. |

`GET /jobs/{id}/result` tải artifact của job sau khi hoàn tất. Job lưu `Id`, `VideoId`, `JobType`, `Status`, `Progress`, `OutputPath`, `Result`, `ErrorMessage`, `CreatedAt` và `UpdatedAt` trong collection `Jobs`, tách biệt collection `Videos`.

Các route tương ứng cũng có tiền tố `/api`, ví dụ `/api/videos` và `/api/jobs/{id}`. Ngoài ra `GET /health` trả `{"status":"Healthy"}` khi MongoDB, RabbitMQ và bucket MinIO đều sẵn sàng; nếu một dependency lỗi thì trả HTTP 503.

### Thử nhanh bằng PowerShell

Upload file (dùng `curl.exe` để gọi curl thay vì alias PowerShell):

```powershell
curl.exe -i -H "X-Correlation-ID: demo-video-001" -F "file=@C:\path\to\sample.mp4" http://localhost:8081/videos
```

Lưu `videoId` trong JSON response, sau đó tạo job:

```powershell
curl.exe -i -H "X-Correlation-ID: demo-video-001" -H "Content-Type: application/json" -d '{"jobType":"Metadata"}' -X POST http://localhost:8081/videos/{videoId}/jobs
```

Lưu tiếp `jobId` trong response để tra cứu:

```powershell
curl.exe http://localhost:8081/videos/{videoId}
curl.exe http://localhost:8081/jobs/{jobId}
curl.exe http://localhost:8081/videos
curl.exe http://localhost:8081/videos?status=Completed
curl.exe -L -o result.json http://localhost:8081/jobs/{jobId}/result
curl.exe http://localhost:8081/health
```

Có thể thay `/videos` bằng `/api/videos` trong mọi route tương ứng. Upload hiện lưu file và đăng ký video; bước tạo job là request riêng.

## Cấu hình

`docker-compose.yml` nhận thông tin kết nối và credential qua biến môi trường được nạp từ `.env`:

- `MONGODB_CONNECTION_STRING`
- `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`
- `RABBITMQ_DEFAULT_USER`, `RABBITMQ_DEFAULT_PASS`, `RABBITMQ_URI`

`appsettings.json` không chứa connection string, host hay credential. Khi chạy app ngoài Docker, cần cấp cùng các cấu hình bằng environment variables, ví dụ `MongoDB__ConnectionString`, `MinIO__Endpoint`, `MinIO__AccessKey`, `MinIO__SecretKey` và `RabbitMQ__Uri`.

## Build ngoài Docker

```powershell
dotnet build src/Video.API/Video.API.csproj
dotnet build src/Video.Worker/Video.Worker.csproj
dotnet build src/Web.Client/Web.Client.csproj
dotnet build UNGDUNGPHANTAN.sln
```

Khi chạy trực tiếp, cấu hình host/credentials phải được cung cấp qua environment. Các dịch vụ MongoDB, MinIO và RabbitMQ cần sẵn sàng trước khi chạy API/Worker.

Sau khi Compose đã chạy, kiểm thử end-to-end gồm tạo MP4 thật, chạy Thumbnail/Metadata, kiểm tra filter, pause/resume Worker và xác nhận DLQ:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\scripts\validate-e2e.ps1
```

## Quan sát và dừng

```powershell
docker compose ps
docker compose logs -f video-api video-worker
docker compose logs -f rabbitmq
docker compose down
```

RabbitMQ Management: [http://localhost:15672](http://localhost:15672). Queue chính là `video-uploaded`, retry queue là `video-uploaded.retry`, DLQ là `video-uploaded.dead`. Mặc định Worker chạy tối đa 3 lần retry sau lần xử lý ban đầu, mỗi lần chờ 5 giây.

Các named volume giữ dữ liệu sau `docker compose down`. Chỉ chạy `docker compose down -v` khi chủ ý xóa dữ liệu MongoDB, MinIO và RabbitMQ.

## Phạm vi xử lý hiện tại

Worker tải object nguồn từ MinIO, cập nhật tiến độ 25%, 60%, 100% trong MongoDB và ghi artifact kết quả về MinIO. `Thumbnail` dùng FFmpeg để tạo ảnh JPEG; `Metadata` tạo JSON từ thông tin file; `Transcode` tạo JSON minh họa và không thực hiện chuyển mã codec thật. Cần thay thông tin đăng nhập local, bổ sung TLS/secret manager và bật xác thực/phân quyền trước khi triển khai production.

Worker retry kết nối RabbitMQ khi khởi động, dùng durable/persistent delivery, manual ack và prefetch 1. Lỗi tạm thời được đưa qua retry queue TTL; hết retry thì job chuyển Failed và broker dead-letter message vào DLQ bằng reject không requeue. NuGet audit hiện không báo advisory cho các project.