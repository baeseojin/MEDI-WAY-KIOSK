using System.Globalization;
using System.IO;
using System.Text;
using MediWay.Contracts;

namespace MediWay.Kiosk.Services;

public interface IWaitingTicketWriter
{
    Task<string> SaveAsync(VisitResponse visit, string roomName, CancellationToken ct);
}

public sealed class WaitingTicketWriter(string directory) : IWaitingTicketWriter
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MEDI-WAY", "Tickets");

    public async Task<string> SaveAsync(VisitResponse visit, string roomName, CancellationToken ct)
    {
        var folder = Path.GetFullPath(directory);
        Directory.CreateDirectory(folder);
        // Use server-assigned visit identity, never patient input, to construct paths.
        var path = Path.Combine(folder, $"대기표_{visit.ServiceDate:yyyyMMdd}_{visit.QueueNumber:D3}_{visit.Id:N}.txt");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var korea = TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        var utc = visit.CreatedAt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(visit.CreatedAt, DateTimeKind.Utc) : visit.CreatedAt.ToUniversalTime();
        var time = TimeZoneInfo.ConvertTimeFromUtc(utc, korea).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var status = visit.Status switch
        {
            "Waiting" => "대기 중",
            "Called" => "호출됨",
            "InService" => "진료 중",
            "Completed" => "진료 완료",
            "Cancelled" => "접수 취소",
            _ => "상태 확인 필요"
        };
        var text = string.Join(Environment.NewLine,
            "================================", "         MEDI-WAY 대기표", "       가상 병원 시연용", "================================",
            $"대기번호: {visit.QueueNumber:D3}", $"진료실: {OneLine(roomName)}",
            $"진료일: {visit.ServiceDate:yyyy-MM-dd}", $"접수시각: {time} (한국시간)",
            $"발급 당시 접수 상태: {status}", "--------------------------------",
            "전광판의 호출번호를 확인해 주세요.", "진료과별 번호이므로 진료실도 함께 확인해 주세요.",
            "이 파일은 저장 당시 정보이며 실시간으로 갱신되지 않습니다.", "도움이 필요하면 안내 데스크에 문의해 주세요.", "================================", "");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(true), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
