//Copyright (c) 2018 Yardi Technology Limited. Http://www.kooboo.com 
//All rights reserved.
using System.Collections.Generic;
using Kooboo.Api;
using Kooboo.Data;
using Kooboo.Data.Models; 
using System;
using Kooboo.Web.ViewModel;

namespace Kooboo.Web.Api
{
    public class OrganizationApi: IApi
    {
        public string ModelName
        {
            get
            {
                return "organization";
            }
        }

        public bool RequireSite
        {
            get
            {
                return false;
            }
        }

        public bool RequireUser
        {
            get
            {
                return true;
            }
        }
        
        public List<Organization> GetOrganizations(ApiCall call)
        { 
            return GlobalDb.Users.Organizations(call.Context.User.Id); 
        }
             
        public List<User> GetUsers(ApiCall call)
        {
            var org =  GlobalDb.Organization.GetByUser(call.Context.User.Id);
            if (org !=null)
            {
                return GlobalDb.Organization.Users(org.Id);
            }
            return null; 
        }

      
        public SimpleUser ChangeUserOrg(Guid organizationId, ApiCall call)
        {      
            var user = GlobalDb.Users.ChangeOrg(call.Context.User.Id, organizationId);
            if (user != null)
            {
                call.Context.User = user;
            }

            call.Context.Response.DeleteCookie(DataConstants.UserApiSessionKey);

            SimpleUser result = new SimpleUser(user);

            string redirecturl = "/_Admin/Account/Profile"; 

            result.redirectUrl = Kooboo.Web.Service.UserService.GetLoginRedirectUrl(call.Context, user, call.Context.Request.Url, redirecturl); 

            return result;
        }

        [Attributes.RequireParameters("organizationId", "userName")]
        public string AddUser(ApiCall call)
        {
            var userName = call.GetValue("userName");
            var organizationId = call.GetGuidValue("organizationId");
            string message = GlobalDb.Organization.AddUser(userName, organizationId);

            return message??"";
        }

        [Attributes.RequireParameters("organizationId", "userName")]
        public bool DeleteUser(ApiCall call)
        {
            var userName = call.GetValue("userName");
            var organizationId = call.GetGuidValue("organizationId");
            return GlobalDb.Organization.DeleteUser(userName, organizationId);
        } 
          
      
        public Organization GetOrg(ApiCall call)
        { 
            var organizationId = call.Context.User.CurrentOrgId;
             return GlobalDb.Organization.Get(organizationId);  
        }
         
        public Organization GetOwnOrg(ApiCall call)
        {
            return GlobalDb.Organization.GetByUser(call.Context.User.Id); 
        }
         
        [Attributes.RequireParameters("organizationId")] 
        // TODO: should move to user api. 
        public bool IsAdminOfOrganization(ApiCall call)
        {
            string userName = call.Context.User.UserName;
            var organizationId = call.GetGuidValue("organizationId");
            return GlobalDb.Users.IsAdmin(organizationId, call.Context.User.Id); 
        }

        public SimpleUser UpdateDataCenter(string datacenter, ApiCall call)
        {
            // only allow to change your own admin organization. 
            var org = Kooboo.Data.GlobalDb.Organization.GetByUser(call.Context.User.Id);
             
            if (org !=null)
            {
                GlobalDb.Organization.ChangeDataCenter(org.Id, datacenter);
            }             

            call.Context.Response.DeleteCookie(DataConstants.UserApiSessionKey);

            return new SimpleUser(call.Context.User); 

            //if (GlobalDb.Users.IsAdmin(call.Context.User.CurrentOrgId, call.Context.User.Id))
            //{
            //    GlobalDb.Organization.ChangeDataCenter(call.Context.User.CurrentOrgId, datacenter); 
            //}
            // else
            //{
            //    throw new System.Exception(Data.Language.Hardcoded.GetValue("Require administrator rights to change data center", call.Context)); 
            //}     
        }

        public DataCenterInfo GetDataCenter(ApiCall call)
        {
            var org = Kooboo.Data.GlobalDb.Organization.GetByUser(call.Context.User.Id); 

            if (org == null)
            {
                return null; 
            }
             
            var DataCenterUrl = Kooboo.Data.Helper.AccountUrlHelper.Cluster("GetOrgDataCenter");

            DataCenterUrl = DataCenterUrl + "?organizationid=" + org.Id.ToString();

            var list = Lib.Helper.HttpHelper.Get<List<DataCenter>>(DataCenterUrl); 
             
            DataCenterInfo info = new DataCenterInfo();
            info.OrganizationName = org.Name; 

            if (list !=null && list.Count>0)
            {
                var defaultcenter = list.Find(o => o.IsRoot); 
                if (defaultcenter == null)
                {
                    throw new System.Exception(Kooboo.Data.Language.Hardcoded.GetValue("Unexpected error", call.Context)); 
                }

                info.CurrentDatacenterName = defaultcenter.Name;

                info.CurrentDataCenter = defaultcenter.Name;

                foreach (var item in list)
                {
                    if (item.Name != defaultcenter.Name)
                    {
                        info.Availables.Add(new DataCenterInfo.Info() { Country = "China", DisplayName = item.Name, Value = item.Name });  
                        info.AvailableDataCenters.Add(item.Name, item.Name);
                    } 
                } 
            } 
            return info; 
        }

        public class DataCenterInfo
        {
            public string OrganizationName { get; set; }

            public string CurrentDatacenterName { get; set; }

            public string CurrentDataCenter { get; set; }

            public Dictionary<string, string> AvailableDataCenters { get; set; } = new Dictionary<string, string>();


            public List<Info> Availables { get; set; } = new List<Info>(); 


            public class Info
            {
                public string Country { get; set; }

                public string Value { get; set; }

                public string DisplayName { get; set; }
            } 



        }
          
    }
}
