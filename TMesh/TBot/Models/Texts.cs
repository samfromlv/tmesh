using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class Texts
    {
        public string PingReply { get; set; }
        public string NewDeviceWelcomeMessage_Template { get; set; }
        public string NewDeviceWelcomeMessage_Settings { get; set; }
        public string NewDeviceWelcomeMessage_Community { get; set; }
        public string NewDeviceWelcomeMessage_WelcomeUrl { get; set; }
        public string PrivacyDisclaimer { get; set; }
        public string PingReplyWithNetworkUrl { get; set; }
        public string NotRegisteredDeviceReply { get; set; }
    }
}
