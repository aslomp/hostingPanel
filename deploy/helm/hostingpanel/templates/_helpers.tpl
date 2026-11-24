{{- define "hostingpanel.name" -}}
{{- printf "%s-hostingpanel" .Release.Name | trunc 48 | trimSuffix "-" -}}
{{- end -}}
