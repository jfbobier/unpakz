#include <archive.h>
#include <stdio.h>

int main(void)
{
    printf("%s\n", archive_version_details());
    return 0;
}
