public class LegacyHl7Parser
{
    public OrderRecord Parse(string rawMessage)
    {
        var segments = rawMessage.Split('\r');
        var record = new OrderRecord();

        foreach (var segment in segments)
        {
            var fields = segment.Split('|');
            if (fields.Length == 0) continue;

            switch (fields[0])
            {
                case "MSH":
                    record.SendingApplication = fields.Length > 2 ? fields[2] : null;
                    record.MessageType = fields.Length > 8 ? fields[8] : null;
                    break;
                case "PID":
                    record.PatientId = fields.Length > 3 ? fields[3].Split('^')[0] : null;
                    record.PatientName = fields.Length > 5 ? fields[5] : null;
                    break;
                case "ORC":
                    record.OrderControlCode = fields.Length > 1 ? fields[1] : null;
                    record.OrderId = fields.Length > 2 ? fields[2] : null;
                    break;
                case "OBR":
                    record.UniversalServiceId = fields.Length > 4 ? fields[4].Split('^')[0] : null;
                    record.OrderedDateTime = fields.Length > 6 ? fields[6] : null;
                    break;
            }
        }

        return record;
    }

    public class OrderRecord
    {
        public string SendingApplication { get; set; }
        public string MessageType { get; set; }
        public string PatientId { get; set; }
        public string PatientName { get; set; }
        public string OrderControlCode { get; set; }
        public string OrderId { get; set; }
        public string UniversalServiceId { get; set; }
        public string OrderedDateTime { get; set; }
    }
}