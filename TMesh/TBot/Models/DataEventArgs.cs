using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DataEventArgs<T>(T data) : EventArgs
    {
        public T Data { get; set; } = data;
    }
}
