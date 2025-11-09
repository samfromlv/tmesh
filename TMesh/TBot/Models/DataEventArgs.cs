using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Models
{
    public class DataEventArgs<T>: EventArgs
    {
        public T Data { get; set; }
        public DataEventArgs(T data)
        {
            Data = data;
        }
    }
}
