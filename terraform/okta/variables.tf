variable "okta_org_name" {
  description = "Okta organization name (subdomain of okta.com)"
  type        = string
  default     = "integrator-4203185"
}

variable "okta_api_token" {
  description = "Okta API token for managing resources"
  type        = string
  sensitive   = true
}

variable "cloudfront_domain" {
  description = "CloudFront distribution domain for the frontend"
  type        = string
  default     = "d2c95zo6ucmtrt.cloudfront.net"
}
