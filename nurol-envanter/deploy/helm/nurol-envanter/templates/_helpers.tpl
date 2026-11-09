{{- define "ne.name" -}}{{ .Chart.Name }}{{- end -}}
{{- define "ne.fullname" -}}{{ printf "%s" .Release.Name | trunc 50 | trimSuffix "-" }}{{- end -}}
{{- define "ne.labels" -}}
app.kubernetes.io/name: {{ include "ne.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}
{{- define "ne.selector" -}}
app.kubernetes.io/name: {{ include "ne.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}
{{- define "ne.serviceAccount" -}}
{{- if .Values.serviceAccount.create -}}{{ default (include "ne.fullname" .) .Values.serviceAccount.name }}{{- else -}}{{ default "default" .Values.serviceAccount.name }}{{- end -}}
{{- end -}}
{{- define "ne.image" -}}{{ .Values.image.repository }}:{{ .Values.image.tag | default .Chart.AppVersion }}{{- end -}}

{{/* Uygulama ve migration Job'unun ortak ortamı. */}}
{{- define "ne.envFrom" -}}
- configMapRef:
    name: {{ include "ne.fullname" . }}-ayarlar
- secretRef:
    name: {{ required "existingSecret zorunludur (parolalar Secret'ta tutulur)" .Values.existingSecret }}
{{- end -}}
{{- define "ne.caEnv" -}}
{{- if .Values.corporateCa.enabled }}
- name: SSL_CERT_FILE
  value: /etc/ssl/certs/kurum-ca.crt
# OpenLDAP istemcisi (LDAPS) kurum CA'sını bu değişkenden okur.
- name: LDAPTLS_CACERT
  value: /etc/ssl/certs/kurum-ca.crt
{{- end }}
{{- end -}}
{{- define "ne.volumeMounts" -}}
- name: tmp
  mountPath: /tmp
{{- if .Values.corporateCa.enabled }}
- name: kurum-ca
  mountPath: /etc/ssl/certs/kurum-ca.crt
  subPath: {{ .Values.corporateCa.key }}
  readOnly: true
{{- end }}
{{- end -}}
{{- define "ne.volumes" -}}
- name: tmp
  emptyDir: { sizeLimit: 256Mi }
{{- if .Values.corporateCa.enabled }}
- name: kurum-ca
  configMap:
    name: {{ .Values.corporateCa.configMap }}
{{- end }}
{{- end -}}
