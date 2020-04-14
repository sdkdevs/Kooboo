﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using Kooboo.Data.Attributes;
using Kooboo.Data.Context;
using Kooboo.Sites.Logistics.Methods.yto.lib;
using Kooboo.Sites.Logistics.Methods.yto.Model;
using Kooboo.Sites.Logistics.Models;

namespace Kooboo.Sites.Logistics.Methods.yto
{
    public class ZOPLogistics : ILogisticsMethod<YTOSetting>
    {
        public YTOSetting Setting { get; set; }

        public string Name => "YTOLogistics";

        public string DisplayName => Data.Language.Hardcoded.GetValue("YTO", Context);

        public RenderContext Context { get; set; }

        [Description(@"
        <script engine='kscript'>
        var request = {};
    request.senderaddress='软件园三期',
request.sendercity='厦门市',
request.sendercountry='集美区',
request.senderprovince='福建省',
request.sendername='sender',
request.senderphone='111111',
request.receiveraddress='后龙镇',
request.receivercity='泉州市',
request.receivercountry='泉港区',
request.receiverprovince='福建省',
request.receivername='receive',
request.receiverphone='11111111',
        k.logistics.zTOLogistics.createOrder(request)
</script>")]
        [KDefineType(Return = typeof(LogisticsResponse))]
        public ILogisticsResponse CreateOrder(LogisticsRequest request)
        {
            var createRequest = GenerateCreateOderRequest(request);
            LogisticsResponse res = null;

            if (Setting == null)
                return res;

            var apiClient = new YTOClient(this.Setting);
            apiClient.CreateOrder(createRequest);
            return res;
        }

        public LogisticsStatusResponse checkStatus(LogisticsRequest request)
        {
            throw new NotImplementedException();
        }

        [Description(@"
        <script engine='kscript'>
         var request = {};
        request.senderprovince='江苏省',
        request.receiverprovince='福建省',
        request.cargoweight='1',
        k.logistics.YTOLogistics.getPostage(request)
        </script> ")]
        public string GetPostage(LogisticsRequest request)
        {
            if (Setting == null)
            {
                return "";
            }
            var chargeRequest = new ChargeQueryRequest
            {
                StartProvince = request.SenderInfo.Prov,
                StartCity = request.SenderInfo.County,
                EndProvince = request.ReceiverInfo.Prov,
                EndCity = request.ReceiverInfo.County,
                GoodsWeight = request.Weight.ToString()
            };

            var apiClient = new YTOClient(this.Setting);
            var result = apiClient.ChargeQuery(chargeRequest);
            return result;
        }

        private RequestOrder GenerateCreateOderRequest(LogisticsRequest request)
        {
            var requestOrder = new RequestOrder
            {
                Receiver = new PersonalInfo
                {
                    Address = "软件园",
                    City = "厦门市",
                    Prov = "福建省",
                    Name = "receiver",
                    Phone = "11111111111",
                    PostCode = "0"
                },
                Sender = new PersonalInfo
                {
                    Address = "软件园",
                    City = "泉州市",
                    Prov = "福建省",
                    Name = "receiver",
                    Phone = "11111111111",
                    PostCode = "0"
                },
                TxLogisticID = request.Id.ToString("N")
            };
            return new RequestOrder();
        }

        private OrderStatus ConvertStatus(string code)
        {
            var status = OrderStatus.Init;
            switch (code.ToUpper())
            {
            }

            return status;
        }
    }
}