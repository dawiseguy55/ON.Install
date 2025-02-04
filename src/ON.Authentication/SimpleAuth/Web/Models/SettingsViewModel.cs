﻿using ON.Fragments.Authentication;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace ON.Authentication.SimpleAuth.Web.Models
{
    public class SettingsViewModel
    {
        public SettingsViewModel() { }

        public SettingsViewModel(UserRecord user)
        {
            UserName = user.Public.UserName;
            DisplayName = user.Public.DisplayName;
            Email = user.Private.Emails.FirstOrDefault();
        }

        [Display(Name = "User Name")]
        [RegularExpression(@"^[a-zA-Z0-9]+$")]
        [StringLength(20, ErrorMessage = "{0} length must be between {2} and {1}.", MinimumLength = 4)]
        public string UserName { get; set; }

        [Required]
        [Display(Name = "Display Name")]
        [RegularExpression(@"^[a-zA-Z0-9]+$")]
        [StringLength(20, ErrorMessage = "{0} length must be between {2} and {1}.", MinimumLength = 4)]
        public string DisplayName { get; set; }

        [Required, DataType(DataType.EmailAddress), EmailAddress]
        public string Email { get; set; }

        public string ErrorMessage { get; set; }
        public string SuccessMessage { get; set; }
    }
}
