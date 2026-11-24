namespace SharbatlyQMS.Web.ViewModels;

/// <summary>What the request-a-survey dialog shows about the shipment. All of
/// it is read from the arrival; the user types only the reason and comments.</summary>
public class SurveyRequestVm
{
    public long      ArrivalId      { get; set; }
    public string    ArrivalNo      { get; set; } = "";
    /// <summary>Set when the dialog is on the quality order page.</summary>
    public long?     QualityOrderId { get; set; }
    public string?   QualityOrderNo { get; set; }
    public string?   ContainerNo    { get; set; }
    public string?   VendorName     { get; set; }
    public string?   ProductSummary { get; set; }
    public DateTime? ReceiveDate    { get; set; }
}
