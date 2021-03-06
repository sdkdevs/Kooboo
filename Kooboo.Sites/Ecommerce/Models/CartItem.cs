﻿using Kooboo.Sites.Ecommerce.Promotion;
using System;
using System.Collections.Generic;

namespace Kooboo.Sites.Ecommerce.Models
{
   public class CartItem 
    { 
        public Guid ProductVariantId { get; set; }

        /// <summary>
        /// For redundancy
        /// </summary>
        public Guid ProductId { get; set; }

        public int Quantity { get; set; } = 1; 

        public decimal UnitPrice { get; set; }

        public Discount Discount { get; set; }

        public decimal ItemTotal { 
            get
            {
                var total = this.UnitPrice * this.Quantity; 
                
                if (Discount !=null && Discount.Total>0)
                {
                    total = total - Discount.Total; 
                }

                if (total <0)
                {
                    return 0;  
                }
                return total; 
            }

        }
           
    }
}
