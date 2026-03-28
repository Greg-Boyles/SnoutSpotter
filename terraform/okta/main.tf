terraform {
  required_version = ">= 1.5"

  required_providers {
    okta = {
      source  = "okta/okta"
      version = "~> 4.0"
    }
  }
}

provider "okta" {
  org_name  = var.okta_org_name
  base_url  = "okta.com"
  api_token = var.okta_api_token
}

resource "okta_app_oauth" "snoutspotter" {
  label                      = "SnoutSpotter"
  type                       = "browser"
  grant_types                = ["authorization_code"]
  response_types             = ["code"]
  token_endpoint_auth_method = "none"
  pkce_required              = true

  redirect_uris = [
    "https://${var.cloudfront_domain}/login/callback",
    "http://localhost:5173/login/callback",
  ]

  post_logout_redirect_uris = [
    "https://${var.cloudfront_domain}",
    "http://localhost:5173",
  ]

  lifecycle {
    prevent_destroy = true
  }
}
