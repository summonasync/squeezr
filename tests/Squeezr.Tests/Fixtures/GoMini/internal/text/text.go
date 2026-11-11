package text

import (
	"strings"
	"unicode"
)

const DefaultWidth = 40

type Text struct {
	slots []string
}

func (t *Text) Push(chunk string) int {
	return len(strings.TrimSpace(chunk))
}

func Normalize(value string, width int) string {
	if width == 0 {
		width = DefaultWidth
	}
	return strings.TrimSpace(value)[:width]
}

func CountSpace(r rune) bool {
	return unicode.IsSpace(r)
}
